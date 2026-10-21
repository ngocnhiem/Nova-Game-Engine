using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NovaEngine.Interop;
using NovaEngine.InputSystem.Composites;
using NovaEngine.InputSystem.Controls;
using NovaEngine.InputSystem.Interactions;
using NovaEngine.InputSystem.LowLevel;
using NovaEngine.InputSystem.Processors;
using NovaEngine.InputSystem.Utilities;
using NovaEngine.InputSystem.XInput;

namespace NovaEngine.InputSystem
{
    public enum InputDeviceChange { Added, Removed, Disconnected, Reconnected, Enabled, Disabled, UsageChanged, ConfigurationChanged, SoftReset, HardReset }

    public enum InputActionChange
    {
        ActionEnabled, ActionDisabled, ActionMapEnabled, ActionMapDisabled,
        ActionStarted, ActionPerformed, ActionCanceled,
        BoundControlsAboutToChange, BoundControlsChanged,
    }

    /// <summary>
    /// Unity 의 InputSettings — 데드존 · 누름 문턱 · 상호작용 시간 기본값.
    /// Project Settings &gt; Input System Package 의 값 (ProjectSettings/InputSystem.json) 을 Play 마다 읽는다
    /// </summary>
    public class InputSettings : ScriptableObject
    {
        public enum UpdateMode { ProcessEventsInDynamicUpdate = 1, ProcessEventsInFixedUpdate = 2, ProcessEventsManually = 3 }
        public enum BackgroundBehavior { ResetAndDisableNonBackgroundDevices = 0, ResetAndDisableAllDevices = 1, IgnoreFocus = 2 }
        public enum EditorInputBehaviorInPlayMode { PointersAndKeyboardsRespectGameViewFocus = 0, AllDevicesRespectGameViewFocus = 1, AllDeviceInputAlwaysGoesToGameView = 2 }
        public enum ScrollDeltaBehavior { UniformAcrossAllPlatforms = 0, KeepPlatformSpecificInputRange = 1 }

        public UpdateMode updateMode { get; set; } = UpdateMode.ProcessEventsInDynamicUpdate;
        public BackgroundBehavior backgroundBehavior { get; set; } = BackgroundBehavior.ResetAndDisableNonBackgroundDevices;
        public EditorInputBehaviorInPlayMode editorInputBehaviorInPlayMode { get; set; } = EditorInputBehaviorInPlayMode.PointersAndKeyboardsRespectGameViewFocus;
        public ScrollDeltaBehavior scrollDeltaBehavior { get; set; } = ScrollDeltaBehavior.UniformAcrossAllPlatforms;
        public bool compensateForScreenOrientation { get; set; }
        public float defaultDeadzoneMin { get; set; } = 0.125f;
        public float defaultDeadzoneMax { get; set; } = 0.925f;
        public float defaultButtonPressPoint { get; set; } = 0.5f;
        public float buttonReleaseThreshold { get; set; } = 0.75f;
        public float defaultTapTime { get; set; } = 0.2f;
        public float defaultSlowTapTime { get; set; } = 0.5f;
        public float defaultHoldTime { get; set; } = 0.4f;
        public float tapRadius { get; set; } = 5f;
        public float multiTapDelayTime { get; set; } = 0.75f;
        public bool filterNoiseOnCurrent { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeInputState
    {
        public int version;
        public int flags;           // 1 = 자판 · 마우스 입력 포커스, 2 = 마우스가 게임 화면 안, 4 = 앱 포커스, 8 = 터치 화면 (안드로이드)
        public float mouseX, mouseY, mouseDX, mouseDY, scrollX, scrollY;
        public int mouseButtons;
        public fixed int padConnected[8];
        public fixed uint padButtons[8];
        public fixed float padAxes[48];   // 패드마다 lx, ly, rx, ry, lt, rt
        public fixed byte keys[256];
    }

    internal static unsafe class InputNative
    {
        const string Lib = "NovaCore";
        [DllImport(Lib)] internal static extern int NovaInputSys_GetState(NativeInputState* state, int size);
        [DllImport(Lib)] internal static extern void NovaInputSys_SetMotors(int slot, float low, float high);
        [DllImport(Lib)] internal static extern void NovaInputSys_WarpCursor(float x, float y);
        [DllImport(Lib)] internal static extern void NovaInputSys_SetCursor(int lockMode, int visible);
        [DllImport(Lib)] internal static extern int NovaInputSys_GetCursorLock();
        [DllImport(Lib)] internal static extern int NovaPlayerInput_List(ulong* ids, int max);
        [DllImport(Lib)] internal static extern byte* NovaPlayerInput_Settings(ulong id);
    }

    /// <summary>
    /// Unity 의 InputSystem (com.unity.inputsystem) — 장치 · 동작 · 설정의 입구. 프레임 시작 (MonoBehaviour.Update 전) 에 장치를 읽고 동작을 갱신한다.
    /// InputSystem.actions = 프로젝트 동작 (Project Settings &gt; Input System Package — Unity 6 와 같이 Play 때 모두 켜진다)
    /// </summary>
    public static partial class InputSystem
    {
        public static Version version => new Version(1, 11, 2);

        internal static readonly List<InputDevice> s_Devices = new List<InputDevice>();
        static readonly List<InputDevice> s_Disconnected = new List<InputDevice>();
        internal static int s_DeviceVersion = 1;
        internal static int s_UpdateCount;
        static int s_NextDeviceId = 1;
        static InputSettings s_Settings;
        static InputActionAsset s_Actions;
        static bool s_ActionsLoaded;
        static string s_ProjectWidePath;
        internal static readonly List<InputActionMap> s_EnabledMaps = new List<InputActionMap>();
        internal static readonly List<InputActionRebindingExtensions.RebindingOperation> s_Rebinds = new List<InputActionRebindingExtensions.RebindingOperation>();
        static readonly Stopwatch s_Clock = Stopwatch.StartNew();
        static readonly List<(InputDevice device, Action apply)> s_Queued = new List<(InputDevice, Action)>();
        static readonly ObserverList<InputControl> s_AnyButton = new ObserverList<InputControl>();
        static readonly List<Action<InputDevice, InputDeviceChange>> s_OnDeviceChange = new List<Action<InputDevice, InputDeviceChange>>();
        static readonly List<Action<object, InputActionChange>> s_OnActionChange = new List<Action<object, InputActionChange>>();
        static readonly HashSet<string> s_Warned = new HashSet<string>();
        static readonly Dictionary<string, InputDevice> s_Prototypes = new Dictionary<string, InputDevice>(StringComparer.OrdinalIgnoreCase);
        static Keyboard s_HwKeyboard;
        static Mouse s_HwMouse;
        static Touchscreen s_HwTouch;
        static readonly Gamepad[] s_HwPads = new Gamepad[8];
        static bool s_Started;
        static int s_Flags;

        internal static double CurrentTime => s_Clock.Elapsed.TotalSeconds;

        public static ReadOnlyArray<InputDevice> devices => s_Devices.ToArray();
        public static ReadOnlyArray<InputDevice> disconnectedDevices => s_Disconnected.ToArray();
        public static float pollingFrequency { get; set; } = 60f;
        public static bool runInBackground { get; set; }

        public static InputSettings settings
        {
            get => s_Settings ??= LoadSettings();
            set { s_Settings = value ?? throw new ArgumentNullException(nameof(value)); onSettingsChange?.Invoke(); }
        }

        /// <summary>프로젝트 동작 (Unity 6 의 Project-wide Actions). Project Settings &gt; Input System Package 에서 고른 .inputactions</summary>
        public static InputActionAsset actions
        {
            get { EnsureProjectWideActions(); return s_Actions; }
            set
            {
                EnsureProjectWideActions();
                if (ReferenceEquals(s_Actions, value)) return;
                s_Actions?.Disable();
                s_Actions = value;
                s_Actions?.Enable();
                onActionsChange?.Invoke();
            }
        }

        public static event Action onActionsChange;
        public static event Action onSettingsChange;
        public static event Action onBeforeUpdate;
        public static event Action onAfterUpdate;

        public static event Action<InputDevice, InputDeviceChange> onDeviceChange
        {
            add { if (value != null) s_OnDeviceChange.Add(value); }
            remove => s_OnDeviceChange.Remove(value);
        }

        public static event Action<object, InputActionChange> onActionChange
        {
            add { if (value != null) s_OnActionChange.Add(value); }
            remove => s_OnActionChange.Remove(value);
        }

        /// <summary>아무 단추나 눌렸을 때 (Unity 와 같이 InputSystem.onAnyButtonPress.CallOnce(ctrl =&gt; …))</summary>
        public static IObservable<InputControl> onAnyButtonPress => s_AnyButton;

        // ---------------------------------------------------------------- 장치
        public static TDevice AddDevice<TDevice>(string name = null) where TDevice : InputDevice
        {
            var d = (TDevice)Activator.CreateInstance(typeof(TDevice), true);
            AddDeviceInternal(d, name);
            return d;
        }

        public static InputDevice AddDevice(string layout, string name = null, string variants = null)
        {
            Type t = DeviceTypeForLayout(layout);
            if (t == null) throw new ArgumentException($"Cannot find layout '{layout}'", nameof(layout));
            var d = (InputDevice)Activator.CreateInstance(t, true);
            AddDeviceInternal(d, name);
            return d;
        }

        public static void AddDevice(InputDevice device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (device.m_Added) throw new InvalidOperationException($"Device '{device}' has already been added");
            AddDeviceInternal(device, null);
        }

        static void AddDeviceInternal(InputDevice d, string name)
        {
            EnsureStarted();
            d.SetName(UniqueName(string.IsNullOrEmpty(name) ? d.m_Layout : name));
            if (d.m_DeviceId == 0) d.m_DeviceId = s_NextDeviceId++;
            d.m_Added = true;
            s_Devices.Add(d);
            s_DeviceVersion++;
            d.CallAdded();
            d.MakeCurrent();
            NotifyDeviceChange(d, InputDeviceChange.Added);
        }

        public static void RemoveDevice(InputDevice device)
        {
            if (device == null || !device.m_Added) return;
            RemoveDeviceInternal(device, InputDeviceChange.Removed);
        }

        static void RemoveDeviceInternal(InputDevice device, InputDeviceChange change)
        {
            s_Devices.Remove(device);
            device.m_Added = false;
            s_DeviceVersion++;
            device.CallRemoved();
            if (change == InputDeviceChange.Disconnected && !s_Disconnected.Contains(device)) s_Disconnected.Add(device);
            NotifyDeviceChange(device, change);
        }

        public static void FlushDisconnectedDevices() => s_Disconnected.Clear();

        public static void EnableDevice(InputDevice device)
        {
            if (device == null || device.m_Enabled) return;
            device.m_Enabled = true;
            NotifyDeviceChange(device, InputDeviceChange.Enabled);
        }

        public static void DisableDevice(InputDevice device, bool keepSendingEvents = false)
        {
            if (device == null || !device.m_Enabled) return;
            device.m_Enabled = false;
            Array.Clear(device.m_State, 0, device.m_StateSize);
            NotifyDeviceChange(device, InputDeviceChange.Disabled);
        }

        public static void ResetDevice(InputDevice device, bool alsoResetDontResetControls = false)
        {
            if (device == null) return;
            Array.Clear(device.m_State, 0, device.m_StateSize);
            NotifyDeviceChange(device, InputDeviceChange.SoftReset);
        }

        public static TDevice GetDevice<TDevice>() where TDevice : InputDevice
        {
            TDevice best = null;
            foreach (var d in s_Devices)
                if (d is TDevice t && (best == null || t.m_LastUpdateTime > best.m_LastUpdateTime)) best = t;
            return best;
        }

        public static InputDevice GetDevice(string nameOrLayout)
        {
            foreach (var d in s_Devices)
                if (string.Equals(d.name, nameOrLayout, StringComparison.OrdinalIgnoreCase)) return d;
            foreach (var d in s_Devices)
                if (d.HasLayout(nameOrLayout)) return d;
            return null;
        }

        public static InputDevice GetDeviceById(int deviceId)
        {
            foreach (var d in s_Devices)
                if (d.m_DeviceId == deviceId) return d;
            return null;
        }

        public static List<InputDevice> GetUnpairedInputDevices()
        {
            var list = new List<InputDevice>();
            foreach (var d in s_Devices)
                if (!PlayerInputRuntime.IsPaired(d)) list.Add(d);
            return list;
        }

        public static InputControl FindControl(string path)
        {
            var l = new List<InputControl>();
            InputControlPath.FindControls(path, s_Devices, l);
            return l.Count > 0 ? l[0] : null;
        }

        public static InputControl[] FindControls(string path)
        {
            var l = new List<InputControl>();
            InputControlPath.FindControls(path, s_Devices, l);
            return l.ToArray();
        }

        /// <summary>장치 상태를 바꾼다 (다음 Update 에 — Unity 와 같이 시험 · 가상 장치용). 하드웨어 장치는 그 프레임만 덮인다</summary>
        public static void QueueStateEvent<TState>(InputDevice device, TState state, double time = -1) where TState : struct, IInputStateTypeInfo
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (!(state is IStateWriter w) || !w.CanWrite(device))
                throw new ArgumentException($"State '{typeof(TState).Name}' does not fit device '{device}'", nameof(state));
            object boxed = state;
            QueueChange(device, () => ((IStateWriter)boxed).Write(device));
        }

        public static void QueueDeltaStateEvent<TDelta>(InputControl control, TDelta delta, double time = -1) where TDelta : struct
        {
            if (control is InputControl<TDelta> c) InputState.Change(c, delta);
            else throw new ArgumentException($"Control '{control}' does not take '{typeof(TDelta).Name}'", nameof(delta));
        }

        internal static void QueueChange(InputDevice device, Action apply)
        {
            if (device == null) return;
            s_Queued.Add((device, apply));
        }

        public static void ResetHaptics() { foreach (var g in Gamepad.all) g.ResetHaptics(); }
        public static void PauseHaptics() { foreach (var g in Gamepad.all) g.PauseHaptics(); }
        public static void ResumeHaptics() { foreach (var g in Gamepad.all) g.ResumeHaptics(); }

        public static List<InputAction> ListEnabledActions()
        {
            var l = new List<InputAction>();
            ListEnabledActions(l);
            return l;
        }

        public static int ListEnabledActions(List<InputAction> actions)
        {
            int n = 0;
            foreach (var m in s_EnabledMaps)
                foreach (var a in m.m_Actions)
                    if (a.m_Enabled) { actions.Add(a); n++; }
            return n;
        }

        // ---------------------------------------------------------------- 등록 (processor · interaction · composite · layout)
        static readonly Dictionary<string, Type> s_ProcessorTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Type> s_InteractionTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Type> s_CompositeTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Type> s_LayoutTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> s_BuiltinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, InputProcessor[]> s_ProcessorCache = new Dictionary<string, InputProcessor[]>(StringComparer.OrdinalIgnoreCase);

        static InputSystem()
        {
            RegisterProcessor<AxisDeadzoneProcessor>();
            RegisterProcessor<StickDeadzoneProcessor>();
            RegisterProcessor<ClampProcessor>();
            RegisterProcessor<InvertProcessor>();
            RegisterProcessor<InvertVector2Processor>();
            RegisterProcessor<InvertVector3Processor>();
            RegisterProcessor<NormalizeProcessor>();
            RegisterProcessor<NormalizeVector2Processor>();
            RegisterProcessor<NormalizeVector3Processor>();
            RegisterProcessor<ScaleProcessor>();
            RegisterProcessor<ScaleVector2Processor>();
            RegisterProcessor<ScaleVector3Processor>();
            RegisterProcessor<CompensateDirectionProcessor>();
            RegisterInteraction<HoldInteraction>();
            RegisterInteraction<TapInteraction>();
            RegisterInteraction<SlowTapInteraction>();
            RegisterInteraction<MultiTapInteraction>();
            RegisterInteraction<PressInteraction>();
            RegisterBindingComposite<AxisComposite>("1DAxis");
            RegisterBindingComposite<AxisComposite>("Axis");
            RegisterBindingComposite<Vector2Composite>("2DVector");
            RegisterBindingComposite<Vector2Composite>("Dpad");
            RegisterBindingComposite<Vector3Composite>("3DVector");
            RegisterBindingComposite<ButtonWithOneModifier>("ButtonWithOneModifier");
            RegisterBindingComposite<ButtonWithTwoModifiers>("ButtonWithTwoModifiers");
            RegisterBindingComposite<OneModifierComposite>("OneModifier");
            RegisterBindingComposite<TwoModifiersComposite>("TwoModifiers");
            RegisterLayout<Keyboard>("Keyboard");
            RegisterLayout<Mouse>("Mouse");
            RegisterLayout<Mouse>("Pointer");
            RegisterLayout<Gamepad>("Gamepad");
            RegisterLayout<XInputController>("XInputController");
            RegisterLayout<XInputControllerWindows>("XInputControllerWindows");
            RegisterLayout<Touchscreen>("Touchscreen");
            foreach (var k in s_ProcessorTypes.Keys) s_BuiltinNames.Add("p:" + k);
            foreach (var k in s_InteractionTypes.Keys) s_BuiltinNames.Add("i:" + k);
            foreach (var k in s_CompositeTypes.Keys) s_BuiltinNames.Add("c:" + k);
            foreach (var k in s_LayoutTypes.Keys) s_BuiltinNames.Add("l:" + k);
        }

        static string Trim(string name, string suffix) => name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length ? name.Substring(0, name.Length - suffix.Length) : name;
        internal static string NameOfProcessor(Type t) => Trim(t.Name, "Processor");
        internal static string NameOfInteraction(Type t) => Trim(t.Name, "Interaction");

        public static void RegisterProcessor<T>(string name = null) => RegisterProcessor(typeof(T), name);
        public static void RegisterProcessor(Type type, string name = null)
        {
            if (type == null || !typeof(InputProcessor).IsAssignableFrom(type)) throw new ArgumentException("Type must derive from InputProcessor", nameof(type));
            s_ProcessorTypes[name ?? NameOfProcessor(type)] = type;
            s_ProcessorCache.Clear();
        }

        public static void RegisterInteraction<T>(string name = null) => RegisterInteraction(typeof(T), name);
        public static void RegisterInteraction(Type type, string name = null)
        {
            if (type == null || !typeof(IInputInteraction).IsAssignableFrom(type)) throw new ArgumentException("Type must implement IInputInteraction", nameof(type));
            s_InteractionTypes[name ?? NameOfInteraction(type)] = type;
        }

        public static void RegisterBindingComposite<T>(string name = null) => RegisterBindingComposite(typeof(T), name);
        public static void RegisterBindingComposite(Type type, string name)
        {
            if (type == null || !typeof(InputBindingComposite).IsAssignableFrom(type)) throw new ArgumentException("Type must derive from InputBindingComposite", nameof(type));
            s_CompositeTypes[name ?? Trim(type.Name, "Composite")] = type;
        }

        public static void RegisterLayout<T>(string name = null) where T : InputDevice => s_LayoutTypes[name ?? typeof(T).Name] = typeof(T);

        public static Type TryGetProcessor(string name) => name != null && s_ProcessorTypes.TryGetValue(name, out var t) ? t : null;
        public static Type TryGetInteraction(string name) => name != null && s_InteractionTypes.TryGetValue(name, out var t) ? t : null;
        public static Type TryGetBindingComposite(string name) => name != null && s_CompositeTypes.TryGetValue(name, out var t) ? t : null;
        public static IEnumerable<string> ListProcessors() => s_ProcessorTypes.Keys;
        public static IEnumerable<string> ListInteractions() => s_InteractionTypes.Keys;
        public static IEnumerable<string> ListBindingComposites() => s_CompositeTypes.Keys;
        public static IEnumerable<string> ListLayouts() => s_LayoutTypes.Keys;

        static Type DeviceTypeForLayout(string layout) => layout != null && s_LayoutTypes.TryGetValue(layout, out var t) ? t : null;

        internal static InputProcessor[] ParseProcessors(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<InputProcessor>();
            if (s_ProcessorCache.TryGetValue(text, out var cached)) return cached;
            var list = new List<InputProcessor>();
            foreach (var np in NameAndParameters.ParseMultiple(text))
            {
                var t = TryGetProcessor(np.name);
                if (t == null) { WarnOnce($"No processor called '{np.name}'"); continue; }
                var p = (InputProcessor)Activator.CreateInstance(t, true);
                ParamUtil.Apply(p, np.parameters);
                list.Add(p);
            }
            var arr = list.ToArray();
            s_ProcessorCache[text] = arr;
            return arr;
        }

        internal static IInputInteraction[] ParseInteractions(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<IInputInteraction>();
            var list = new List<IInputInteraction>();
            foreach (var np in NameAndParameters.ParseMultiple(text))
            {
                var t = TryGetInteraction(np.name);
                if (t == null) { WarnOnce($"No interaction called '{np.name}'"); continue; }
                var i = (IInputInteraction)Activator.CreateInstance(t, true);
                ParamUtil.Apply(i, np.parameters);
                list.Add(i);
            }
            return list.ToArray();
        }

        internal static InputBindingComposite CreateComposite(string name)
        {
            var t = TryGetBindingComposite(name);
            return t == null ? null : (InputBindingComposite)Activator.CreateInstance(t, true);
        }

        // 레이아웃 원형 (표시 이름 · 경로 풀기 — 실제 장치가 없어도)
        internal static InputDevice Prototype(string layout)
        {
            if (string.IsNullOrEmpty(layout)) return null;
            if (s_Prototypes.TryGetValue(layout, out var d)) return d;
            Type t = DeviceTypeForLayout(layout);
            d = t != null ? (InputDevice)Activator.CreateInstance(t, true) : null;
            s_Prototypes[layout] = d;
            return d;
        }

        internal static void WarnOnce(string message)
        {
            if (s_Warned.Add(message)) Debug.LogWarning(message);
        }

        // ---------------------------------------------------------------- 알림
        static void NotifyDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (s_OnDeviceChange.Count == 0) return;
            foreach (var cb in s_OnDeviceChange.ToArray())
            {
                try { cb(device, change); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        internal static void NotifyActionChange(object target, InputActionChange change)
        {
            if (s_OnActionChange.Count == 0) return;
            foreach (var cb in s_OnActionChange.ToArray())
            {
                try { cb(target, change); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        internal static void NotifyActionTriggered(in InputAction.CallbackContext ctx)
        {
            if (ctx.m_Phase == InputActionPhase.Started) ctx.m_Action.m_StartedCount++;
            else if (ctx.m_Phase == InputActionPhase.Performed) ctx.m_Action.m_PerformedCount++;
            else if (ctx.m_Phase == InputActionPhase.Canceled) ctx.m_Action.m_CanceledCount++;
            if (s_OnActionChange.Count == 0) return;
            var change = ctx.m_Phase == InputActionPhase.Started ? InputActionChange.ActionStarted : ctx.m_Phase == InputActionPhase.Performed ? InputActionChange.ActionPerformed : InputActionChange.ActionCanceled;
            NotifyActionChange(ctx.m_Action, change);
        }

        internal static void OnMapEnabled(InputActionMap map)
        {
            if (map == null || s_EnabledMaps.Contains(map)) return;
            s_EnabledMaps.Add(map);
            if (!map.m_IsSingleton) NotifyActionChange(map, InputActionChange.ActionMapEnabled);
        }

        internal static void OnMapMaybeDisabled(InputActionMap map)
        {
            if (map == null || map.enabled) return;
            if (s_EnabledMaps.Remove(map) && !map.m_IsSingleton) NotifyActionChange(map, InputActionChange.ActionMapDisabled);
        }

        // ---------------------------------------------------------------- 갱신
        /// <summary>장치를 읽고 동작을 갱신 (보통 엔진이 프레임마다 — InputSettings.updateMode = ProcessEventsManually 이면 직접)</summary>
        public static void Update()
        {
            EnsureStarted();
            s_UpdateCount++;
            double now = CurrentTime;
            try { onBeforeUpdate?.Invoke(); } catch (Exception e) { Debug.LogException(e); }

            for (int i = 0; i < s_Devices.Count; i++) s_Devices[i].BeginFrame();
            PollHardware();
            if (s_Queued.Count > 0)
            {
                var q = s_Queued.ToArray();
                s_Queued.Clear();
                foreach (var (device, apply) in q)
                    if (device.m_Added)
                    {
                        try { apply(); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
            }

            foreach (var d in s_Devices)
            {
                if (!d.StateChanged()) continue;
                d.m_UpdatedFrame = s_UpdateCount;
                d.m_LastUpdateTime = now;
                if (HasNonNoisyChange(d)) d.MakeCurrent();
            }
            if (s_AnyButton.Count > 0)
                foreach (var d in s_Devices.ToArray())
                {
                    if (d.m_UpdatedFrame != s_UpdateCount) continue;
                    foreach (var c in d.allControls)
                        if (c is ButtonControl b && !b.synthetic && !b.noisy && b.wasPressedThisFrame) s_AnyButton.Notify(b);
                }

            if (s_Rebinds.Count > 0)
                foreach (var op in s_Rebinds.ToArray()) op.Process(now);

            PlayerInputRuntime.BeforeActions();
            for (int i = 0; i < s_EnabledMaps.Count; i++)
            {
                var map = s_EnabledMaps[i];
                try { map.UpdateFrame(now); }
                catch (Exception e) { Debug.LogException(e); }
            }
            try { onAfterUpdate?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

        static bool HasNonNoisyChange(InputDevice d)
        {
            foreach (var c in d.allControls)
            {
                if (c.noisy || c.synthetic || c.m_Children.Length > 0) continue;
                if (c is AxisControl a && a.m_Slot >= 0 && a.m_Reader == null && d.m_State[a.m_Slot] != d.m_Prev[a.m_Slot]) return true;
            }
            return false;
        }

        // Awake · Start 에서 동작을 켜면 (첫 Update 전) 장치가 아직 없다 — 하드웨어 장치를 한 번 만들어 둔다
        static bool s_HardwareReady;
        internal static void EnsureDevices()
        {
            if (s_HardwareReady) return;
            EnsureStarted();
            PollHardware();
        }

        // 엔진 상태 → 하드웨어 장치
        static unsafe void PollHardware()
        {
            NativeInputState st = default;
            int ok;
            try { ok = InputNative.NovaInputSys_GetState(&st, sizeof(NativeInputState)); }
            catch (EntryPointNotFoundException) { return; }
            catch (DllNotFoundException) { return; }
            if (ok == 0) return;
            s_HardwareReady = true;
            s_Flags = st.flags;
            bool android = Application.platform == RuntimePlatform.Android;
            if (!android)
            {
                if (s_HwKeyboard == null)
                {
                    s_HwKeyboard = new Keyboard { m_Native = true };
                    AddDeviceInternal(s_HwKeyboard, null);
                }
                if (s_HwMouse == null)
                {
                    s_HwMouse = new Mouse { m_Native = true };
                    AddDeviceInternal(s_HwMouse, null);
                }
            }
            if (s_HwKeyboard != null && s_HwKeyboard.m_Added && s_HwKeyboard.m_Enabled)
                foreach (var k in s_HwKeyboard.allKeys)
                    if (k != null && k.m_VirtualKey > 0) k.m_Device.m_State[k.m_Slot] = st.keys[k.m_VirtualKey] != 0 ? 1f : 0f;
            if (s_HwMouse != null && s_HwMouse.m_Added && s_HwMouse.m_Enabled)
            {
                var m = s_HwMouse;
                var s = m.m_State;
                s[m.position.x.m_Slot] = st.mouseX;
                s[m.position.y.m_Slot] = st.mouseY;
                s[m.delta.x.m_Slot] = st.mouseDX;
                s[m.delta.y.m_Slot] = st.mouseDY;
                s[m.scroll.x.m_Slot] = st.scrollX;
                s[m.scroll.y.m_Slot] = st.scrollY;
                s[m.leftButton.m_Slot] = (st.mouseButtons & 1) != 0 ? 1f : 0f;
                s[m.rightButton.m_Slot] = (st.mouseButtons & 2) != 0 ? 1f : 0f;
                s[m.middleButton.m_Slot] = (st.mouseButtons & 4) != 0 ? 1f : 0f;
                s[m.backButton.m_Slot] = (st.mouseButtons & 8) != 0 ? 1f : 0f;
                s[m.forwardButton.m_Slot] = (st.mouseButtons & 16) != 0 ? 1f : 0f;
                s[m.pressure.m_Slot] = (st.mouseButtons & 1) != 0 ? 1f : 0f;
            }
            for (int i = 0; i < 8; i++)
            {
                bool connected = st.padConnected[i] != 0;
                var pad = s_HwPads[i];
                if (connected && (pad == null || !pad.m_Added))
                {
                    if (pad == null)
                    {
                        pad = i < 4 ? new XInputControllerWindows() : new Gamepad();
                        pad.m_Native = i < 4;
                        pad.m_NativeSlot = i;
                        if (i >= 4) pad.m_Description = new InputDeviceDescription { interfaceName = "NOVA CLI", deviceClass = "Gamepad", product = "Injected Gamepad " + (i - 4) };
                        s_HwPads[i] = pad;
                        AddDeviceInternal(pad, null);
                    }
                    else
                    {
                        s_Disconnected.Remove(pad);
                        pad.m_Added = true;
                        s_Devices.Add(pad);
                        s_DeviceVersion++;
                        pad.CallAdded();
                        pad.MakeCurrent();
                        NotifyDeviceChange(pad, InputDeviceChange.Reconnected);
                    }
                }
                else if (!connected && pad != null && pad.m_Added)
                {
                    Array.Clear(pad.m_State, 0, pad.m_StateSize);
                    RemoveDeviceInternal(pad, InputDeviceChange.Disconnected);
                    continue;
                }
                if (!connected || pad == null) continue;
                uint b = st.padButtons[i];
                var ps = pad.m_State;
                // XInput 단추 비트
                ps[pad.dpad.up.m_Slot] = (b & 0x0001) != 0 ? 1 : 0;
                ps[pad.dpad.down.m_Slot] = (b & 0x0002) != 0 ? 1 : 0;
                ps[pad.dpad.left.m_Slot] = (b & 0x0004) != 0 ? 1 : 0;
                ps[pad.dpad.right.m_Slot] = (b & 0x0008) != 0 ? 1 : 0;
                ps[pad.startButton.m_Slot] = (b & 0x0010) != 0 ? 1 : 0;
                ps[pad.selectButton.m_Slot] = (b & 0x0020) != 0 ? 1 : 0;
                ps[pad.leftStickButton.m_Slot] = (b & 0x0040) != 0 ? 1 : 0;
                ps[pad.rightStickButton.m_Slot] = (b & 0x0080) != 0 ? 1 : 0;
                ps[pad.leftShoulder.m_Slot] = (b & 0x0100) != 0 ? 1 : 0;
                ps[pad.rightShoulder.m_Slot] = (b & 0x0200) != 0 ? 1 : 0;
                ps[pad.buttonSouth.m_Slot] = (b & 0x1000) != 0 ? 1 : 0;
                ps[pad.buttonEast.m_Slot] = (b & 0x2000) != 0 ? 1 : 0;
                ps[pad.buttonWest.m_Slot] = (b & 0x4000) != 0 ? 1 : 0;
                ps[pad.buttonNorth.m_Slot] = (b & 0x8000) != 0 ? 1 : 0;
                ps[pad.leftStick.x.m_Slot] = st.padAxes[i * 6 + 0];
                ps[pad.leftStick.y.m_Slot] = st.padAxes[i * 6 + 1];
                ps[pad.rightStick.x.m_Slot] = st.padAxes[i * 6 + 2];
                ps[pad.rightStick.y.m_Slot] = st.padAxes[i * 6 + 3];
                ps[pad.leftTrigger.m_Slot] = st.padAxes[i * 6 + 4];
                ps[pad.rightTrigger.m_Slot] = st.padAxes[i * 6 + 5];
            }
            if (android || (st.flags & 8) != 0)
                PollTouches();
        }

        // 안드로이드 손가락 (Platform.NovaInput_*) → Touchscreen
        static readonly int[] s_TouchIds = new int[Touchscreen.MaxTouches];
        static readonly bool[] s_TouchUsed = new bool[Touchscreen.MaxTouches];
        static int s_PrimaryId = -1;
        static unsafe void PollTouches()
        {
            if (s_HwTouch == null)
            {
                s_HwTouch = new Touchscreen { m_Native = true };
                AddDeviceInternal(s_HwTouch, null);
            }
            var ts = s_HwTouch;
            var s = ts.m_State;
            int n = Platform.NovaInput_TouchCount();
            var seen = new bool[Touchscreen.MaxTouches];
            float* t = stackalloc float[7];
            double now = CurrentTime;
            for (int k = 0; k < n; k++)
            {
                if (Platform.NovaInput_GetTouch(k, t) == 0) continue;
                int id = (int)t[0];
                int slot = -1;
                for (int j = 0; j < Touchscreen.MaxTouches; j++) if (s_TouchUsed[j] && s_TouchIds[j] == id) { slot = j; break; }
                if (slot < 0) for (int j = 0; j < Touchscreen.MaxTouches; j++) if (!s_TouchUsed[j]) { slot = j; s_TouchUsed[j] = true; s_TouchIds[j] = id; break; }
                if (slot < 0) continue;
                seen[slot] = true;
                var tc = ts.touches[slot];
                // 옛 TouchPhase (Began 0, Moved 1, Stationary 2, Ended 3, Canceled 4) → InputSystem.TouchPhase
                int legacy = (int)t[5];
                TouchPhase phase = legacy == 0 ? TouchPhase.Began : legacy == 1 ? TouchPhase.Moved : legacy == 2 ? TouchPhase.Stationary : legacy == 3 ? TouchPhase.Ended : TouchPhase.Canceled;
                bool began = phase == TouchPhase.Began;
                s[tc.touchId.m_Slot] = id + 1;
                s[tc.position.x.m_Slot] = t[1];
                s[tc.position.y.m_Slot] = t[2];
                s[tc.delta.x.m_Slot] = t[3];
                s[tc.delta.y.m_Slot] = t[4];
                s[tc.phase.m_Slot] = (int)phase;
                s[tc.tapCount.m_Slot] = t[6];
                bool down = phase == TouchPhase.Began || phase == TouchPhase.Moved || phase == TouchPhase.Stationary;
                s[tc.press.m_Slot] = down ? 1 : 0;
                s[tc.pressure.m_Slot] = down ? 1 : 0;
                if (began)
                {
                    s[tc.startPosition.x.m_Slot] = t[1];
                    s[tc.startPosition.y.m_Slot] = t[2];
                    s[tc.startTime.m_Slot] = (float)now;
                    if (s_PrimaryId < 0) s_PrimaryId = id;
                }
                // tap: 짧고 (tapTime) 가까이 (tapRadius) 놓으면 한 프레임
                bool tap = false;
                if (phase == TouchPhase.Ended)
                {
                    float dx = t[1] - s[tc.startPosition.x.m_Slot], dy = t[2] - s[tc.startPosition.y.m_Slot];
                    tap = now - s[tc.startTime.m_Slot] <= settings.defaultTapTime * 2.5 && dx * dx + dy * dy <= settings.tapRadius * settings.tapRadius * 16;
                }
                s[tc.tap.m_Slot] = tap ? 1 : 0;
                if (id == s_PrimaryId) CopyTouch(ts, tc, ts.primaryTouch);
                if (phase == TouchPhase.Ended || phase == TouchPhase.Canceled)
                {
                    s_TouchUsed[slot] = false;
                    if (id == s_PrimaryId) s_PrimaryId = -1;
                }
            }
            for (int j = 0; j < Touchscreen.MaxTouches; j++)
            {
                if (seen[j]) continue;
                var tc = ts.touches[j];
                if (s[tc.press.m_Slot] != 0 || s[tc.phase.m_Slot] != 0)
                {
                    s[tc.press.m_Slot] = 0;
                    s[tc.phase.m_Slot] = (int)TouchPhase.None;
                    s[tc.tap.m_Slot] = 0;
                    s[tc.delta.x.m_Slot] = s[tc.delta.y.m_Slot] = 0;
                }
                s_TouchUsed[j] = false;
            }
            if (s_PrimaryId < 0 && n == 0)
            {
                var p = ts.primaryTouch;
                if (s[p.phase.m_Slot] == (int)TouchPhase.Ended || s[p.phase.m_Slot] == (int)TouchPhase.Canceled || s[p.press.m_Slot] != 0)
                {
                    bool wasEnded = s[p.phase.m_Slot] == (int)TouchPhase.Ended || s[p.phase.m_Slot] == (int)TouchPhase.Canceled;
                    s[p.press.m_Slot] = 0;
                    s[p.phase.m_Slot] = wasEnded ? (int)TouchPhase.None : (int)TouchPhase.Ended;
                    s[p.tap.m_Slot] = 0;
                    s[p.delta.x.m_Slot] = s[p.delta.y.m_Slot] = 0;
                }
            }
        }

        static void CopyTouch(Touchscreen ts, TouchControl from, TouchControl to)
        {
            var s = ts.m_State;
            void C(InputControl a, InputControl b)
            {
                if (a is AxisControl x && b is AxisControl y) s[y.m_Slot] = s[x.m_Slot];
                else if (a is IntegerControl xi && b is IntegerControl yi) s[yi.m_Slot] = s[xi.m_Slot];
                else if (a is DoubleControl xd && b is DoubleControl yd) s[yd.m_Slot] = s[xd.m_Slot];
                else if (a is TouchPhaseControl xp && b is TouchPhaseControl yp) s[yp.m_Slot] = s[xp.m_Slot];
            }
            C(from.press, to.press); C(from.touchId, to.touchId); C(from.position.x, to.position.x); C(from.position.y, to.position.y);
            C(from.delta.x, to.delta.x); C(from.delta.y, to.delta.y); C(from.pressure, to.pressure); C(from.phase, to.phase);
            C(from.tap, to.tap); C(from.tapCount, to.tapCount); C(from.startPosition.x, to.startPosition.x); C(from.startPosition.y, to.startPosition.y);
            C(from.startTime, to.startTime);
        }

        static string UniqueName(string baseName)
        {
            string n = baseName;
            for (int i = 1; ; i++)
            {
                bool taken = false;
                foreach (var d in s_Devices) if (string.Equals(d.name, n, StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
                if (!taken) return n;
                n = baseName + i;
            }
        }

        // ---------------------------------------------------------------- 세션 (Play 시작 · 끝)
        static void EnsureStarted()
        {
            if (s_Started) return;
            s_Started = true;
            s_Settings ??= LoadSettings();
        }

        static void EnsureProjectWideActions()
        {
            if (s_ActionsLoaded) return;
            s_ActionsLoaded = true;
            if (string.IsNullOrEmpty(s_ProjectWidePath)) ReadProjectSettings();
            if (!string.IsNullOrEmpty(s_ProjectWidePath))
            {
                s_Actions = InputActionAssetCache.Load(s_ProjectWidePath);
                s_Actions?.Enable();
            }
        }

        static JsonElement? s_ProjectJson;
        static void ReadProjectSettings()
        {
            s_ProjectJson = null;
            try
            {
                byte[] bytes = ScriptableAssets.ReadBytes("ProjectSettings/InputSystem.json");
                if (bytes == null) return;
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                s_ProjectJson = doc.RootElement.Clone();
                if (doc.RootElement.TryGetProperty("projectWideActions", out var p) && p.ValueKind == JsonValueKind.String) s_ProjectWidePath = p.GetString();
            }
            catch (Exception e) { Debug.LogWarning("ProjectSettings/InputSystem.json: " + e.Message); }
        }

        static InputSettings LoadSettings()
        {
            var s = ScriptableObject.CreateInstance<InputSettings>();
            s.name = "InputSettings";
            if (s_ProjectJson == null) ReadProjectSettings();
            if (s_ProjectJson is JsonElement root && root.TryGetProperty("settings", out var j) && j.ValueKind == JsonValueKind.Object)
            {
                float F(string key, float def) => j.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : def;
                int I(string key, int def) => j.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
                s.updateMode = (InputSettings.UpdateMode)Math.Clamp(I("updateMode", 1), 1, 3);
                s.backgroundBehavior = (InputSettings.BackgroundBehavior)Math.Clamp(I("backgroundBehavior", 0), 0, 2);
                s.defaultDeadzoneMin = F("defaultDeadzoneMin", s.defaultDeadzoneMin);
                s.defaultDeadzoneMax = F("defaultDeadzoneMax", s.defaultDeadzoneMax);
                s.defaultButtonPressPoint = Math.Max(0.0001f, F("defaultButtonPressPoint", s.defaultButtonPressPoint));
                s.buttonReleaseThreshold = Math.Clamp(F("buttonReleaseThreshold", s.buttonReleaseThreshold), 0.01f, 1f);
                s.defaultTapTime = F("defaultTapTime", s.defaultTapTime);
                s.defaultSlowTapTime = F("defaultSlowTapTime", s.defaultSlowTapTime);
                s.defaultHoldTime = F("defaultHoldTime", s.defaultHoldTime);
                s.tapRadius = F("tapRadius", s.tapRadius);
                s.multiTapDelayTime = F("multiTapDelayTime", s.multiTapDelayTime);
            }
            return s;
        }

        // Bridge.BeginFrame (MonoBehaviour.Update 전)
        internal static void OnEngineFrame()
        {
            EnsureStarted();
            EnsureProjectWideActions();
            if (settings.updateMode == InputSettings.UpdateMode.ProcessEventsManually) return;
            Update();
        }

        // Play 끝 · 스크립트 다시 읽기: Unity 의 도메인 다시 읽기처럼 처음 상태로 (동작 끄기, 게임이 만든 장치 · 등록 지우기, 캐시 비우기)
        internal static void ResetSession()
        {
            foreach (var op in s_Rebinds.ToArray()) op.Cancel();
            s_Rebinds.Clear();
            PlayerInputRuntime.Reset();
            foreach (var m in s_EnabledMaps.ToArray())
                foreach (var a in m.m_Actions) { try { a.Disable(); } catch { } }
            s_EnabledMaps.Clear();
            foreach (var d in s_Devices.ToArray())
                if (!d.m_Native && !(d.m_NativeSlot >= 4)) RemoveDeviceInternal(d, InputDeviceChange.Removed);
            foreach (var g in Gamepad.all) g.ResetHaptics();
            foreach (var d in s_Devices) { Array.Clear(d.m_State, 0, d.m_StateSize); Array.Clear(d.m_Prev, 0, d.m_StateSize); }
            s_Queued.Clear();
            s_OnDeviceChange.Clear();
            s_OnActionChange.Clear();
            onActionsChange = null;
            onSettingsChange = null;
            onBeforeUpdate = null;
            onAfterUpdate = null;
            s_Actions = null;
            s_ActionsLoaded = false;
            s_ProjectWidePath = null;
            s_ProjectJson = null;
            s_Settings = null;
            s_Warned.Clear();
            InputActionAssetCache.Clear();
            // 게임 어셈블리가 등록한 processor · interaction · composite · layout (수집 가능한 컨텍스트를 붙잡지 않게)
            void Drop(Dictionary<string, Type> d, string prefix)
            {
                foreach (var k in new List<string>(d.Keys))
                    if (!s_BuiltinNames.Contains(prefix + k)) d.Remove(k);
            }
            Drop(s_ProcessorTypes, "p:");
            Drop(s_InteractionTypes, "i:");
            Drop(s_CompositeTypes, "c:");
            Drop(s_LayoutTypes, "l:");
            s_ProcessorCache.Clear();
            CursorState.Reset();
        }

        // ---------------------------------------------------------------- NOVA CLI (nova input info · action · rebind)
        internal static string Command(string json)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var a = doc.RootElement;
            string op = a.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : "info";
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                switch (op)
                {
                    case "info":
                    case "devices":
                        WriteInfo(w);
                        break;
                    case "action":
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var action = FindAnyAction(name);
                        if (action == null) { w.WriteString("error", "no enabled action '" + name + "' (map/action)"); break; }
                        WriteActionFields(w, action, true);
                        break;
                    }
                    case "rebind":
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        int index = a.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : -1;
                        string group = a.TryGetProperty("group", out var gr) && gr.ValueKind == JsonValueKind.String ? gr.GetString() : null;
                        var action = FindAnyAction(name);
                        if (action == null) { w.WriteString("error", "no action '" + name + "'"); break; }
                        bool wasEnabled = action.enabled;
                        action.Disable();
                        var opn = action.PerformInteractiveRebinding(index).WithCancelingThrough("<Keyboard>/escape");
                        if (group != null) opn.WithBindingGroup(group);
                        opn.OnComplete(r => { Debug.Log($"[Input] rebind {action.name} -> {action.GetBindingDisplayString()}"); if (wasEnabled) action.Enable(); r.Dispose(); })
                           .OnCancel(r => { Debug.Log($"[Input] rebind {action.name} canceled"); if (wasEnabled) action.Enable(); r.Dispose(); })
                           .Start();
                        w.WriteString("rebinding", action.ToString());
                        break;
                    }
                    default:
                        w.WriteString("error", "unknown input op '" + op + "' (info, action, rebind)");
                        break;
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static InputAction FindAnyAction(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // PlayerInput 의 동작 → 프로젝트 동작 → 켜진 아무 맵 (만든 C# 클래스도 같은 맵 이름을 가진다)
            var fromPlayer = PlayerInputRuntime.FindAction(name);
            if (fromPlayer != null) return fromPlayer;
            var fromProject = actions?.FindAction(name);
            if (fromProject != null) return fromProject;
            foreach (var m in s_EnabledMaps)
            {
                int slash = name.IndexOf('/');
                if (slash > 0 && !string.Equals(m.name, name.Substring(0, slash), StringComparison.OrdinalIgnoreCase)) continue;
                var a = m.FindAction(slash > 0 ? name.Substring(slash + 1) : name);
                if (a != null) return a;
            }
            return null;
        }

        static void WriteInfo(Utf8JsonWriter w)
        {
            w.WriteNumber("updateCount", s_UpdateCount);
            w.WriteNumber("flags", s_Flags);
            w.WriteString("projectWideActions", s_ProjectWidePath ?? string.Empty);
            w.WriteStartArray("devices");
            foreach (var d in s_Devices)
            {
                w.WriteStartObject();
                w.WriteString("name", d.name);
                w.WriteString("layout", d.layout);
                w.WriteNumber("id", d.deviceId);
                w.WriteBoolean("native", d.native);
                w.WriteBoolean("current", ReferenceEquals(d, Keyboard.current) || ReferenceEquals(d, Mouse.current) || ReferenceEquals(d, Gamepad.current) || ReferenceEquals(d, Touchscreen.current));
                w.WriteStartArray("active");
                foreach (var c in d.allControls)
                {
                    if (c.m_Children.Length > 0 || c.synthetic) continue;
                    if (c is AxisControl ax && Math.Abs(ax.ReadValue()) > 0.001f && !c.noisy)
                        w.WriteStringValue(c.name + "=" + ax.ReadValue().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                w.WriteEndArray();
                if (d is Mouse mouse)
                {
                    var p = mouse.position.ReadValue();
                    w.WriteString("position", p.x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "," + p.y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("actions");
            foreach (var m in s_EnabledMaps)
                foreach (var act in m.m_Actions)
                    if (act.m_Enabled)
                    {
                        w.WriteStartObject();
                        WriteActionFields(w, act, false);
                        w.WriteEndObject();
                    }
            w.WriteEndArray();
            PlayerInputRuntime.WriteInfo(w);
        }

        static void WriteActionFields(Utf8JsonWriter w, InputAction act, bool full)
        {
            w.WriteString("name", (act.actionMap != null ? act.actionMap.name + "/" : "") + act.name);
            w.WriteString("type", act.type.ToString());
            w.WriteString("phase", act.phase.ToString());
            object v = act.IsInProgress() ? act.m_Value.ToObject() : null;
            w.WriteString("value", v == null ? "" : FormatValue(v));
            w.WriteBoolean("pressed", act.IsPressed());
            w.WriteString("control", act.activeControl?.path ?? string.Empty);
            w.WriteNumber("started", act.m_StartedCount);
            w.WriteNumber("performed", act.m_PerformedCount);
            w.WriteNumber("canceled", act.m_CanceledCount);
            w.WriteNumber("pressedFrame", act.m_PressedFrame);
            w.WriteNumber("releasedFrame", act.m_ReleasedFrame);
            if (full)
            {
                w.WriteString("display", act.GetBindingDisplayString());
                w.WriteStartArray("bindings");
                foreach (var b in act.bindings) w.WriteStringValue(b.ToString() + (b.overridePath != null ? " (override)" : ""));
                w.WriteEndArray();
                w.WriteStartArray("controls");
                foreach (var c in act.controls) w.WriteStringValue(c.path);
                w.WriteEndArray();
                w.WriteNumber("frame", s_UpdateCount);
            }
        }

        internal static string FormatValue(object v)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            switch (v)
            {
                case float f: return f.ToString("0.####", ci);
                case Vector2 v2: return v2.x.ToString("0.####", ci) + "," + v2.y.ToString("0.####", ci);
                case Vector3 v3: return v3.x.ToString("0.####", ci) + "," + v3.y.ToString("0.####", ci) + "," + v3.z.ToString("0.####", ci);
            }
            return v.ToString();
        }
    }

    // .inputactions 읽기 · 캐시 (같은 경로 = 같은 인스턴스, Unity 의 에셋처럼)
    internal static class InputActionAssetCache
    {
        static readonly Dictionary<string, InputActionAsset> s_Cache = new Dictionary<string, InputActionAsset>(StringComparer.OrdinalIgnoreCase);

        internal static InputActionAsset Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = ScriptableAssets.Normalize(path);
            if (s_Cache.TryGetValue(key, out var a)) return a;
            byte[] bytes = ScriptableAssets.ReadBytes(key);
            if (bytes == null)
            {
                InputSystem.WarnOnce("Input Actions asset not found: " + key);
                return null;
            }
            try
            {
                a = InputActionAsset.FromJson(Encoding.UTF8.GetString(bytes));
                a.name = Path.GetFileNameWithoutExtension(key);
                a.m_AssetPath = key;
                s_Cache[key] = a;
                return a;
            }
            catch (Exception e)
            {
                Debug.LogError("Cannot read Input Actions asset " + key + ": " + e.Message);
                return null;
            }
        }

        internal static void Clear() => s_Cache.Clear();
    }
}
