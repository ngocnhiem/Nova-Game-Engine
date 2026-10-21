using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NovaEngine.Events;
using NovaEngine.Interop;
using NovaEngine.InputSystem.Controls;
using NovaEngine.InputSystem.Utilities;

namespace NovaEngine.InputSystem
{
    /// <summary>Unity 의 PlayerNotifications — PlayerInput 이 동작을 알리는 방법</summary>
    public enum PlayerNotifications
    {
        /// <summary>같은 GameObject 의 스크립트에 "On" + 동작 이름 (OnMove(InputValue value))</summary>
        SendMessages = 0,
        /// <summary>자식 GameObject 까지</summary>
        BroadcastMessages = 1,
        /// <summary>Inspector 의 동작마다 이벤트 (InputAction.CallbackContext)</summary>
        InvokeUnityEvents = 2,
        /// <summary>C# 이벤트 onActionTriggered</summary>
        InvokeCSharpEvents = 3,
    }

    /// <summary>Unity 의 InputValue — SendMessages 의 메시지 인자 (OnJump(InputValue value) { value.isPressed })</summary>
    public class InputValue
    {
        internal InputAction.CallbackContext? m_Context;

        public object Get() => m_Context?.ReadValueAsObject();

        public TValue Get<TValue>() where TValue : struct
        {
            if (!m_Context.HasValue) throw new InvalidOperationException("Values can only be retrieved while in message callbacks");
            return m_Context.Value.ReadValue<TValue>();
        }

        public bool isPressed => m_Context.HasValue && m_Context.Value.ReadValueAsButton();
    }

    /// <summary>
    /// Unity 의 PlayerInput — 동작 에셋 하나를 플레이어 하나에: 기본 맵을 켜고, 컨트롤 스킴 (자판+마우스 ↔ 게임패드) 을 알아서 바꾸고,
    /// 동작을 메시지 (OnMove · OnJump) · Unity 이벤트 · C# 이벤트로 알린다. Inspector: Add Component &gt; Input &gt; Player Input
    /// </summary>
    [NativeComponent("PlayerInput")]
    public class PlayerInput : Behaviour
    {
        public const string DeviceLostMessage = "OnDeviceLost";
        public const string DeviceRegainedMessage = "OnDeviceRegained";
        public const string ControlsChangedMessage = "OnControlsChanged";

        internal PlayerInput() { }

        PlayerInputRuntime.State S => PlayerInputRuntime.Get(nativeId);

        public InputActionAsset actions
        {
            get => S?.actions;
            set { var s = S; if (s != null) PlayerInputRuntime.SetActions(s, value); }
        }

        public InputActionMap currentActionMap
        {
            get => S?.currentMap;
            set { var s = S; if (s != null) PlayerInputRuntime.SwitchMap(s, value); }
        }

        public string defaultActionMap
        {
            get => S?.defaultMap;
            set { var s = S; if (s != null) s.defaultMap = value; }
        }

        public string defaultControlScheme
        {
            get => S?.defaultScheme;
            set { var s = S; if (s != null) s.defaultScheme = value; }
        }

        public string currentControlScheme => S?.currentScheme;

        public PlayerNotifications notificationBehavior
        {
            get => S?.behavior ?? PlayerNotifications.SendMessages;
            set { var s = S; if (s != null) s.behavior = value; }
        }

        public bool neverAutoSwitchControlSchemes
        {
            get => S?.neverAutoSwitch ?? false;
            set { var s = S; if (s != null) s.neverAutoSwitch = value; }
        }

        public int playerIndex => S?.playerIndex ?? -1;
        public int splitScreenIndex => -1;
        public bool inputIsActive => S?.inputActive ?? false;
        public bool hasMissingRequiredDevices => S?.missingDevices ?? false;
        public ReadOnlyArray<InputDevice> devices => S != null ? S.devices.ToArray() : Array.Empty<InputDevice>();
        public Camera camera { get; set; }

        public ReadOnlyArray<ActionEvent> actionEvents => S != null ? S.actionEvents.ToArray() : Array.Empty<ActionEvent>();
        public DeviceLostEvent deviceLostEvent => S?.deviceLostEvent;
        public DeviceRegainedEvent deviceRegainedEvent => S?.deviceRegainedEvent;
        public ControlsChangedEvent controlsChangedEvent => S?.controlsChangedEvent;

        public event Action<InputAction.CallbackContext> onActionTriggered
        {
            add { var s = S; if (s != null && value != null) s.onActionTriggered.Add(value); }
            remove { S?.onActionTriggered.Remove(value); }
        }
        public event Action<PlayerInput> onDeviceLost
        {
            add { var s = S; if (s != null && value != null) s.onDeviceLost.Add(value); }
            remove { S?.onDeviceLost.Remove(value); }
        }
        public event Action<PlayerInput> onDeviceRegained
        {
            add { var s = S; if (s != null && value != null) s.onDeviceRegained.Add(value); }
            remove { S?.onDeviceRegained.Remove(value); }
        }
        public event Action<PlayerInput> onControlsChanged
        {
            add { var s = S; if (s != null && value != null) s.onControlsChanged.Add(value); }
            remove { S?.onControlsChanged.Remove(value); }
        }

        public void ActivateInput() { var s = S; if (s != null) PlayerInputRuntime.Activate(s); }
        public void DeactivateInput() { var s = S; if (s != null) PlayerInputRuntime.Deactivate(s); }
        public void PassivateInput() => DeactivateInput();

        public void SwitchCurrentActionMap(string mapNameOrId)
        {
            var s = S;
            if (s?.actions == null) throw new InvalidOperationException($"Cannot switch to action map '{mapNameOrId}' as PlayerInput has no actions");
            var map = s.actions.FindActionMap(mapNameOrId);
            if (map == null) throw new ArgumentException($"Cannot find action map '{mapNameOrId}' in actions '{s.actions}'", nameof(mapNameOrId));
            PlayerInputRuntime.SwitchMap(s, map);
        }

        public bool SwitchCurrentControlScheme(params InputDevice[] devices)
        {
            var s = S;
            if (s?.actions == null || devices == null || devices.Length == 0) return false;
            foreach (var scheme in s.actions.controlSchemes)
            {
                bool all = true;
                foreach (var d in devices) all &= scheme.SupportsDevice(d);
                if (all) return PlayerInputRuntime.SwitchScheme(s, scheme, new List<InputDevice>(devices));
            }
            return false;
        }

        public void SwitchCurrentControlScheme(string controlScheme, params InputDevice[] devices)
        {
            var s = S;
            if (s?.actions == null) throw new InvalidOperationException("PlayerInput has no actions");
            var scheme = s.actions.FindControlScheme(controlScheme);
            if (!scheme.HasValue) throw new ArgumentException($"No control scheme '{controlScheme}' in '{s.actions}'", nameof(controlScheme));
            var list = devices != null && devices.Length > 0 ? new List<InputDevice>(devices) : scheme.Value.PickDevices(InputSystem.s_Devices, null, PlayerInputRuntime.DevicesOfOthers(s));
            PlayerInputRuntime.SwitchScheme(s, scheme.Value, list ?? new List<InputDevice>());
        }

        public TDevice GetDevice<TDevice>() where TDevice : InputDevice
        {
            var s = S;
            if (s == null) return null;
            foreach (var d in s.devices) if (d is TDevice t) return t;
            return null;
        }

        public static ReadOnlyArray<PlayerInput> all => PlayerInputRuntime.All();
        public static bool isSinglePlayer => PlayerInputRuntime.Count <= 1;
        public static PlayerInput GetPlayerByIndex(int playerIndex) => PlayerInputRuntime.ByIndex(playerIndex);
        public static PlayerInput FindFirstPairedToDevice(InputDevice device) => PlayerInputRuntime.PairedTo(device);

        [Serializable]
        public class ActionEvent : UnityEvent<InputAction.CallbackContext>
        {
            internal string m_ActionId;
            internal string m_ActionName;
            public string actionId => m_ActionId;
            public string actionName => m_ActionName;
            public ActionEvent() { }
            public ActionEvent(InputAction action)
            {
                m_ActionId = action.idString;
                m_ActionName = (action.actionMap != null ? action.actionMap.name + "/" : "") + action.name;
            }
            public ActionEvent(Guid actionGUID, string name = null) { m_ActionId = actionGUID.ToString(); m_ActionName = name; }
        }

        [Serializable] public class DeviceLostEvent : UnityEvent<PlayerInput> { }
        [Serializable] public class DeviceRegainedEvent : UnityEvent<PlayerInput> { }
        [Serializable] public class ControlsChangedEvent : UnityEvent<PlayerInput> { }
    }

    // PlayerInput 컴포넌트의 실행 상태 (C# 래퍼는 GetComponent 마다 새로 — 상태는 GameObject id 마다)
    internal static unsafe class PlayerInputRuntime
    {
        internal sealed class PersistentCall
        {
            public ulong target;
            public string cls, method, paramType, argument;
            public bool dynamic;
            public int callState = 2;
        }

        internal sealed class State
        {
            public ulong id;
            public InputActionAsset actions;
            public string assetPath;
            public string defaultScheme, defaultMap;
            public PlayerNotifications behavior;
            public bool neverAutoSwitch;
            public int playerIndex;
            public InputActionMap currentMap;
            public string currentScheme;
            public readonly List<InputDevice> devices = new List<InputDevice>();
            public readonly List<InputDevice> lostDevices = new List<InputDevice>();
            public bool inputActive, missingDevices;
            public readonly List<PlayerInput.ActionEvent> actionEvents = new List<PlayerInput.ActionEvent>();
            public readonly Dictionary<string, List<PersistentCall>> actionCalls = new Dictionary<string, List<PersistentCall>>(StringComparer.OrdinalIgnoreCase);
            public List<PersistentCall> lostCalls = new List<PersistentCall>(), regainedCalls = new List<PersistentCall>(), changedCalls = new List<PersistentCall>();
            public PlayerInput.DeviceLostEvent deviceLostEvent = new PlayerInput.DeviceLostEvent();
            public PlayerInput.DeviceRegainedEvent deviceRegainedEvent = new PlayerInput.DeviceRegainedEvent();
            public PlayerInput.ControlsChangedEvent controlsChangedEvent = new PlayerInput.ControlsChangedEvent();
            public readonly List<Action<InputAction.CallbackContext>> onActionTriggered = new List<Action<InputAction.CallbackContext>>();
            public readonly List<Action<PlayerInput>> onDeviceLost = new List<Action<PlayerInput>>();
            public readonly List<Action<PlayerInput>> onDeviceRegained = new List<Action<PlayerInput>>();
            public readonly List<Action<PlayerInput>> onControlsChanged = new List<Action<PlayerInput>>();
            public readonly InputValue value = new InputValue();
            public Action<InputAction.CallbackContext> hook;
            public bool ownsActions;
            public PlayerInput Wrapper() => new PlayerInput { m_Id = id };
        }

        static readonly Dictionary<ulong, State> s_States = new Dictionary<ulong, State>();
        static readonly List<State> s_Ordered = new List<State>();
        static readonly ulong[] s_Ids = new ulong[256];
        static int s_SyncedFrame = -1;

        internal static int Count => s_Ordered.Count;

        internal static State Get(ulong id)
        {
            Sync();
            return s_States.TryGetValue(id, out var s) ? s : null;
        }

        internal static ReadOnlyArray<PlayerInput> All()
        {
            Sync();
            var arr = new PlayerInput[s_Ordered.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = s_Ordered[i].Wrapper();
            return arr;
        }

        internal static PlayerInput ByIndex(int index)
        {
            Sync();
            foreach (var s in s_Ordered) if (s.playerIndex == index) return s.Wrapper();
            return null;
        }

        internal static PlayerInput PairedTo(InputDevice device)
        {
            Sync();
            foreach (var s in s_Ordered) if (s.devices.Contains(device)) return s.Wrapper();
            return null;
        }

        internal static bool IsPaired(InputDevice d)
        {
            foreach (var s in s_Ordered) if (s.devices.Contains(d)) return true;
            return false;
        }

        internal static List<InputDevice> DevicesOfOthers(State me)
        {
            var l = new List<InputDevice>();
            foreach (var s in s_Ordered) if (s != me) l.AddRange(s.devices);
            return l;
        }

        internal static InputAction FindAction(string name)
        {
            foreach (var s in s_Ordered)
            {
                var a = s.actions?.FindAction(name);
                if (a != null) return a;
            }
            return null;
        }

        internal static void Reset()
        {
            foreach (var s in s_Ordered.ToArray()) Remove(s);
            s_States.Clear();
            s_Ordered.Clear();
            s_SyncedFrame = -1;
        }

        // 프레임마다 동작 갱신 전: 컴포넌트 목록 맞추기 · 장치 잃음/되찾음 · 스킴 자동 전환
        internal static void BeforeActions()
        {
            Sync();
            foreach (var s in s_Ordered.ToArray())
            {
                CheckDevices(s);
                if (!s.neverAutoSwitch && s_Ordered.Count == 1 && s.actions != null && s.actions.m_ControlSchemes.Length > 0)
                    AutoSwitch(s);
            }
        }

        static void Sync()
        {
            if (s_SyncedFrame == InputSystem.s_UpdateCount) return;
            s_SyncedFrame = InputSystem.s_UpdateCount;
            InputSystem.EnsureDevices();
            int n;
            fixed (ulong* p = s_Ids)
            {
                try { n = InputNative.NovaPlayerInput_List(p, s_Ids.Length); }
                catch (EntryPointNotFoundException) { return; }
                catch (DllNotFoundException) { return; }
            }
            var seen = new HashSet<ulong>();
            for (int i = 0; i < n; i++)
            {
                ulong id = s_Ids[i];
                seen.Add(id);
                if (!s_States.ContainsKey(id)) Create(id);
            }
            foreach (var s in s_Ordered.ToArray())
                if (!seen.Contains(s.id)) Remove(s);
        }

        static void Create(ulong id)
        {
            var s = new State { id = id };
            string json = Native.Str(InputNative.NovaPlayerInput_Settings(id)) ?? "{}";
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                s.assetPath = Str("actions");
                s.defaultScheme = Str("defaultScheme");
                s.defaultMap = Str("defaultMap");
                s.behavior = r.TryGetProperty("behavior", out var b) && b.ValueKind == JsonValueKind.Number ? (PlayerNotifications)Math.Clamp(b.GetInt32(), 0, 3) : PlayerNotifications.SendMessages;
                s.neverAutoSwitch = r.TryGetProperty("neverAutoSwitch", out var nv) && nv.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
                    foreach (var e in evs.EnumerateArray())
                    {
                        string aid = e.TryGetProperty("actionId", out var x) ? x.GetString() : null;
                        if (string.IsNullOrEmpty(aid)) continue;
                        s.actionCalls[aid] = ReadCalls(e);
                    }
                if (r.TryGetProperty("deviceLost", out var dl)) s.lostCalls = ReadCalls(dl);
                if (r.TryGetProperty("deviceRegained", out var dr)) s.regainedCalls = ReadCalls(dr);
                if (r.TryGetProperty("controlsChanged", out var cc)) s.changedCalls = ReadCalls(cc);
            }
            catch (Exception e) { Debug.LogWarning("PlayerInput settings: " + e.Message); }

            // 플레이어 번호: 비어 있는 가장 작은 수
            int index = 0;
            while (s_Ordered.Exists(o => o.playerIndex == index)) index++;
            s.playerIndex = index;
            s_States[id] = s;
            s_Ordered.Add(s);
            s_Ordered.Sort((a, c) => a.playerIndex.CompareTo(c.playerIndex));

            var asset = !string.IsNullOrEmpty(s.assetPath) ? InputActionAssetCache.Load(s.assetPath) : null;
            SetActions(s, asset);
        }

        static List<PersistentCall> ReadCalls(JsonElement e)
        {
            var list = new List<PersistentCall>();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("calls", out var calls)) e = calls;
            if (e.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in e.EnumerateArray())
            {
                string method = c.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                int dot = method.LastIndexOf('.');
                list.Add(new PersistentCall
                {
                    target = c.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetUInt64() : 0,
                    cls = dot > 0 ? method.Substring(0, dot) : "",
                    method = dot > 0 ? method.Substring(dot + 1) : method,
                    paramType = c.TryGetProperty("paramType", out var pt) ? pt.GetString() ?? "" : "",
                    argument = c.TryGetProperty("argument", out var ar) ? ar.GetString() ?? "" : "",
                    dynamic = c.TryGetProperty("dynamic", out var dy) && dy.ValueKind == JsonValueKind.True,
                    callState = c.TryGetProperty("callState", out var cs) && cs.ValueKind == JsonValueKind.Number ? cs.GetInt32() : 2,
                });
            }
            return list;
        }

        static void Remove(State s)
        {
            Deactivate(s);
            Unhook(s);
            s.devices.Clear();
            s_States.Remove(s.id);
            s_Ordered.Remove(s);
        }

        internal static void SetActions(State s, InputActionAsset asset)
        {
            Deactivate(s);
            Unhook(s);
            s.actions = null;
            s.currentMap = null;
            s.currentScheme = null;
            s.devices.Clear();
            if (asset == null) return;
            // 둘째 플레이어부터 같은 에셋이면 복사 (장치를 따로 묶는다 — Unity 와 같이)
            bool shared = s_Ordered.Exists(o => o != s && ReferenceEquals(o.actions, asset));
            s.actions = shared ? asset.CloneAsset() : asset;
            s.ownsActions = shared;
            s.actionEvents.Clear();
            foreach (var a in s.actions)
                s.actionEvents.Add(new PlayerInput.ActionEvent(a));
            Hook(s);
            InitScheme(s);
            Activate(s);
        }

        static void Hook(State s)
        {
            s.hook = ctx => OnActionTriggered(s, ctx);
            foreach (var m in s.actions.m_ActionMaps) m.actionTriggered += s.hook;
        }

        static void Unhook(State s)
        {
            if (s.actions == null || s.hook == null) return;
            foreach (var m in s.actions.m_ActionMaps) m.actionTriggered -= s.hook;
            s.hook = null;
        }

        static void InitScheme(State s)
        {
            var schemes = s.actions.m_ControlSchemes;
            if (schemes.Length == 0)
            {
                // 스킴이 없으면: 혼자면 모든 장치, 여럿이면 다른 플레이어가 쓰지 않는 장치
                s.currentScheme = null;
                if (s_Ordered.Count > 1)
                {
                    var others = DevicesOfOthers(s);
                    foreach (var d in InputSystem.s_Devices) if (!others.Contains(d)) s.devices.Add(d);
                    s.actions.devices = s.devices.ToArray();
                }
                else
                    s.actions.devices = null;
                return;
            }
            var exclude = DevicesOfOthers(s);
            if (!string.IsNullOrEmpty(s.defaultScheme))
            {
                var d = s.actions.FindControlScheme(s.defaultScheme);
                if (d.HasValue)
                {
                    var picked = d.Value.PickDevices(InputSystem.s_Devices, null, exclude);
                    if (picked != null) { SwitchScheme(s, d.Value, picked, notify: false); return; }
                }
            }
            foreach (var scheme in schemes)
            {
                var picked = scheme.PickDevices(InputSystem.s_Devices, null, exclude);
                if (picked != null && picked.Count > 0) { SwitchScheme(s, scheme, picked, notify: false); return; }
            }
            // 맞는 장치가 없음 — 첫 스킴 (장치가 오면 자동 전환)
            s.missingDevices = true;
            SwitchScheme(s, string.IsNullOrEmpty(s.defaultScheme) || !s.actions.FindControlScheme(s.defaultScheme).HasValue ? schemes[0] : s.actions.FindControlScheme(s.defaultScheme).Value, new List<InputDevice>(), notify: false);
        }

        internal static bool SwitchScheme(State s, InputControlScheme scheme, List<InputDevice> devices, bool notify = true)
        {
            if (s.actions == null) return false;
            s.currentScheme = scheme.name;
            s.devices.Clear();
            s.devices.AddRange(devices);
            s.lostDevices.Clear();
            s.missingDevices = devices.Count == 0;
            foreach (var m in s.actions.m_ActionMaps) m.m_NoIgnoreOnce = notify;
            s.actions.bindingMask = InputBinding.MaskByGroup(scheme.bindingGroup);
            s.actions.devices = s.devices.ToArray();
            if (notify) ControlsChanged(s);
            return true;
        }

        static void ControlsChanged(State s)
        {
            var wrapper = s.Wrapper();
            if (s.behavior == PlayerNotifications.SendMessages || s.behavior == PlayerNotifications.BroadcastMessages)
                SendMessage(s, PlayerInput.ControlsChangedMessage, wrapper, s.behavior == PlayerNotifications.BroadcastMessages);
            else if (s.behavior == PlayerNotifications.InvokeUnityEvents)
            {
                s.controlsChangedEvent.Invoke(wrapper);
                InvokeCalls(s.changedCalls, wrapper);
            }
            else Invoke(s.onControlsChanged, wrapper);
        }

        static void Invoke(List<Action<PlayerInput>> list, PlayerInput p)
        {
            foreach (var cb in list.ToArray())
            {
                try { cb(p); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        internal static void Activate(State s)
        {
            if (s.actions == null) return;
            if (s.currentMap == null && !string.IsNullOrEmpty(s.defaultMap))
                s.currentMap = s.actions.FindActionMap(s.defaultMap);
            if (s.currentMap != null) s.currentMap.Enable();
            else s.actions.Enable();
            s.inputActive = true;
        }

        internal static void Deactivate(State s)
        {
            if (s.actions == null || !s.inputActive) return;
            // 프로젝트 동작 (InputSystem.actions) 을 같이 쓰면 끄지 않는다 — 다른 코드도 읽는다
            if (!ReferenceEquals(s.actions, InputSystem.actions))
            {
                if (s.currentMap != null) s.currentMap.Disable();
                else s.actions.Disable();
            }
            s.inputActive = false;
        }

        internal static void SwitchMap(State s, InputActionMap map)
        {
            if (s.inputActive && s.currentMap != null && !ReferenceEquals(s.currentMap, map)) s.currentMap.Disable();
            s.currentMap = map;
            if (s.inputActive) map?.Enable();
        }

        static void CheckDevices(State s)
        {
            if (s.actions == null || s.actions.m_ControlSchemes.Length == 0) return;
            bool lost = false;
            for (int i = s.devices.Count - 1; i >= 0; i--)
                if (!s.devices[i].added)
                {
                    s.lostDevices.Add(s.devices[i]);
                    s.devices.RemoveAt(i);
                    lost = true;
                }
            if (lost)
            {
                s.actions.devices = s.devices.ToArray();
                DeviceEvent(s, true);
                return;
            }
            if (s.lostDevices.Count == 0) return;
            bool regained = false;
            for (int i = s.lostDevices.Count - 1; i >= 0; i--)
                if (s.lostDevices[i].added)
                {
                    s.devices.Add(s.lostDevices[i]);
                    s.lostDevices.RemoveAt(i);
                    regained = true;
                }
            if (regained)
            {
                s.actions.devices = s.devices.ToArray();
                DeviceEvent(s, false);
            }
        }

        static void DeviceEvent(State s, bool lost)
        {
            var wrapper = s.Wrapper();
            switch (s.behavior)
            {
                case PlayerNotifications.SendMessages:
                case PlayerNotifications.BroadcastMessages:
                    SendMessage(s, lost ? PlayerInput.DeviceLostMessage : PlayerInput.DeviceRegainedMessage, wrapper, s.behavior == PlayerNotifications.BroadcastMessages);
                    break;
                case PlayerNotifications.InvokeUnityEvents:
                    if (lost) { s.deviceLostEvent.Invoke(wrapper); InvokeCalls(s.lostCalls, wrapper); }
                    else { s.deviceRegainedEvent.Invoke(wrapper); InvokeCalls(s.regainedCalls, wrapper); }
                    break;
                default:
                    Invoke(lost ? s.onDeviceLost : s.onDeviceRegained, wrapper);
                    break;
            }
        }

        // 묶이지 않은 장치를 눈에 띄게 쓰면 그 장치의 스킴으로 (혼자 할 때 — Unity 의 Auto-Switch)
        static void AutoSwitch(State s)
        {
            foreach (var d in InputSystem.s_Devices)
            {
                if (d.m_UpdatedFrame != InputSystem.s_UpdateCount || s.devices.Contains(d) || !Significant(d)) continue;
                foreach (var scheme in s.actions.m_ControlSchemes)
                {
                    if (!scheme.SupportsDevice(d)) continue;
                    var picked = scheme.PickDevices(InputSystem.s_Devices, d, null);
                    if (picked == null || !picked.Contains(d)) continue;
                    SwitchScheme(s, scheme, picked);
                    return;
                }
            }
        }

        static bool Significant(InputDevice d)
        {
            foreach (var c in d.allControls)
            {
                if (c.noisy || c.synthetic || c.m_Children.Length > 0) continue;
                if (c is ButtonControl b && b.wasPressedThisFrame) return true;
                // 축은 처리 전 값으로 (스틱 데드존이 0.5 를 0.47 로 줄여도 눈에 띄는 움직임)
                if (c is AxisControl a && !(c is ButtonControl) && Math.Abs(a.ReadUnprocessedValue()) >= 0.5f && Math.Abs(a.ReadUnprocessedValueFromPreviousFrame()) < 0.5f) return true;
            }
            return false;
        }

        // ---- 알림
        static void OnActionTriggered(State s, InputAction.CallbackContext ctx)
        {
            if (!s.inputActive) return;
            switch (s.behavior)
            {
                case PlayerNotifications.SendMessages:
                case PlayerNotifications.BroadcastMessages:
                {
                    var action = ctx.action;
                    if (!(ctx.performed || (ctx.canceled && action.type == InputActionType.Value))) return;
                    s.value.m_Context = ctx;
                    try { SendMessage(s, MessageName(action), s.value, s.behavior == PlayerNotifications.BroadcastMessages); }
                    finally { s.value.m_Context = null; }
                    break;
                }
                case PlayerNotifications.InvokeUnityEvents:
                {
                    string id = ctx.action.idString;
                    foreach (var e in s.actionEvents)
                        if (string.Equals(e.m_ActionId, id, StringComparison.OrdinalIgnoreCase)) { e.Invoke(ctx); break; }
                    if (s.actionCalls.TryGetValue(id, out var calls)) InvokeCalls(calls, ctx);
                    break;
                }
                case PlayerNotifications.InvokeCSharpEvents:
                    foreach (var cb in s.onActionTriggered.ToArray())
                    {
                        try { cb(ctx); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                    break;
            }
        }

        static readonly Dictionary<InputAction, string> s_MessageNames = new Dictionary<InputAction, string>();
        static string MessageName(InputAction a)
        {
            if (s_MessageNames.TryGetValue(a, out var n)) return n;
            string id = StringUtil.Identifier(a.name);
            n = "On" + (id.Length > 0 ? char.ToUpperInvariant(id[0]) + id.Substring(1) : id);
            if (s_MessageNames.Count > 4096) s_MessageNames.Clear();
            s_MessageNames[a] = n;
            return n;
        }

        // SendMessage (인자 없음 · InputValue · PlayerInput), 메서드는 형식마다 한 번 찾는다
        static readonly Dictionary<(Type, string), MethodInfo> s_Methods = new Dictionary<(Type, string), MethodInfo>();
        static void SendMessage(State s, string name, object arg, bool broadcast)
        {
            Send(s.id, name, arg);
            if (!broadcast) return;
            var t = new Transform(s.id);
            for (int i = 0; i < t.childCount; i++) Broadcast(t.GetChild(i), name, arg);
        }

        static void Broadcast(Transform t, string name, object arg)
        {
            Send(t.m_Id, name, arg);
            for (int i = 0; i < t.childCount; i++) Broadcast(t.GetChild(i), name, arg);
        }

        static void Send(ulong id, string name, object arg)
        {
            foreach (var mb in ScriptRegistry.Get(id).ToArray())
            {
                if (mb == null || !mb.enabled) continue;
                var mi = FindMethod(mb.GetType(), name);
                if (mi == null) continue;
                var ps = mi.GetParameters();
                try
                {
                    if (ps.Length == 0) mi.Invoke(mb, null);
                    else if (arg != null && ps[0].ParameterType.IsInstanceOfType(arg)) mi.Invoke(mb, new[] { arg });
                }
                catch (TargetInvocationException e) { Debug.LogException(e.InnerException ?? e); }
            }
        }

        static MethodInfo FindMethod(Type t, string name)
        {
            if (s_Methods.TryGetValue((t, name), out var mi)) return mi;
            mi = null;
            for (Type c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
            {
                foreach (var m in c.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != name || m.IsGenericMethodDefinition) continue;
                    int n = m.GetParameters().Length;
                    if (n <= 1) { mi = m; break; }
                }
                if (mi != null) break;
            }
            s_Methods[(t, name)] = mi;
            return mi;
        }

        // Inspector 에 넣은 호출 (Unity 의 PersistentCall): 대상 GameObject 의 "클래스.메서드" — 동적 인자 = CallbackContext / PlayerInput
        static void InvokeCalls(List<PersistentCall> calls, object dynamicArg)
        {
            foreach (var c in calls)
            {
                if (c.callState == 0 || c.target == 0 || string.IsNullOrEmpty(c.method)) continue;
                try
                {
                    if (c.cls == "GameObject" && c.method == "SetActive")
                    {
                        new GameObject(c.target).SetActive(c.argument == "1" || c.argument.Equals("true", StringComparison.OrdinalIgnoreCase));
                        continue;
                    }
                    foreach (var mb in ScriptRegistry.Get(c.target).ToArray())
                    {
                        if (mb == null || (!string.IsNullOrEmpty(c.cls) && mb.GetType().Name != c.cls && mb.GetType().FullName != c.cls)) continue;
                        foreach (var mi in mb.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                        {
                            if (mi.Name != c.method) continue;
                            var ps = mi.GetParameters();
                            if (c.dynamic)
                            {
                                if (ps.Length == 1 && dynamicArg != null && ps[0].ParameterType.IsInstanceOfType(dynamicArg)) { mi.Invoke(mb, new[] { dynamicArg }); break; }
                                continue;
                            }
                            if (string.IsNullOrEmpty(c.paramType))
                            {
                                if (ps.Length == 0) { mi.Invoke(mb, null); break; }
                                continue;
                            }
                            if (ps.Length != 1) continue;
                            object arg = c.paramType switch
                            {
                                "int" => int.TryParse(c.argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0,
                                "float" => float.TryParse(c.argument, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f,
                                "bool" => c.argument == "1" || c.argument.Equals("true", StringComparison.OrdinalIgnoreCase),
                                _ => (object)c.argument,
                            };
                            if (ps[0].ParameterType.IsInstanceOfType(arg)) { mi.Invoke(mb, new[] { arg }); break; }
                        }
                    }
                }
                catch (TargetInvocationException e) { Debug.LogException(e.InnerException ?? e); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        internal static void WriteInfo(Utf8JsonWriter w)
        {
            w.WriteStartArray("players");
            foreach (var s in s_Ordered)
            {
                w.WriteStartObject();
                w.WriteNumber("gameObject", s.id);
                w.WriteNumber("index", s.playerIndex);
                w.WriteString("actions", s.assetPath ?? s.actions?.name ?? string.Empty);
                w.WriteString("scheme", s.currentScheme ?? string.Empty);
                w.WriteString("map", s.currentMap?.name ?? string.Empty);
                w.WriteString("behavior", s.behavior.ToString());
                w.WriteBoolean("active", s.inputActive);
                w.WriteStartArray("devices");
                foreach (var d in s.devices) w.WriteStringValue(d.name);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }
}
