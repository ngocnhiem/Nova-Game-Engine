using System;
using System.Collections.Generic;
using NovaEngine.InputSystem.Controls;
using NovaEngine.InputSystem.Processors;
using NovaEngine.InputSystem.Utilities;

namespace NovaEngine.InputSystem
{
    /// <summary>Unity 의 InputDeviceDescription — 장치가 무엇인가 (XInput · Win32 · Android)</summary>
    [Serializable]
    public struct InputDeviceDescription
    {
        public string interfaceName;
        public string deviceClass;
        public string manufacturer;
        public string product;
        public string serial;
        public string version;
        public string capabilities;
        public bool empty => string.IsNullOrEmpty(interfaceName) && string.IsNullOrEmpty(deviceClass) && string.IsNullOrEmpty(product);
        public override string ToString() => string.IsNullOrEmpty(product) ? (interfaceName ?? string.Empty) : product + " (" + interfaceName + ")";
    }

    /// <summary>
    /// Unity 의 InputDevice — 자판 · 마우스 · 게임패드 · 터치 화면. 하드웨어 장치는 엔진이 프레임마다 채우고,
    /// InputSystem.AddDevice 로 만든 장치 (시험 · 가상 입력) 는 InputSystem.QueueStateEvent · InputState.Change 로 바꾼다.
    /// </summary>
    public class InputDevice : InputControl
    {
        public const int InvalidDeviceId = 0;

        internal float[] m_State = new float[8];
        internal float[] m_Prev = new float[8];
        internal int m_StateSize;
        internal readonly List<InputControl> m_All = new List<InputControl>();
        InputControl[] m_AllArray;
        internal string[] m_LayoutChain;
        internal int m_DeviceId;
        internal bool m_Added;
        internal bool m_Enabled = true;
        internal bool m_Native;
        internal int m_NativeSlot = -1;
        internal double m_LastUpdateTime;
        internal int m_UpdatedFrame = -1;
        internal InputDeviceDescription m_Description;
        internal bool m_Dirty;   // 대기 중인 상태 변경 (QueueStateEvent)

        public InputDevice() : this("InputDevice") { }

        internal InputDevice(params string[] layoutChain)
        {
            m_LayoutChain = layoutChain;
            m_Layout = layoutChain[0];
            m_Name = layoutChain[0];
            m_Device = this;
        }

        public int deviceId => m_DeviceId;
        public bool added => m_Added;
        public bool enabled => m_Enabled;
        public bool native => m_Native;
        public bool remote => false;
        public bool canRunInBackground => false;
        public double lastUpdateTime => m_LastUpdateTime;
        public bool wasUpdatedThisFrame => m_UpdatedFrame == InputSystem.s_UpdateCount;
        public InputDeviceDescription description => m_Description;
        public ReadOnlyArray<InputControl> allControls => m_AllArray ??= m_All.ToArray();

        public override Type valueType => typeof(byte[]);
        public override int valueSizeInBytes => m_StateSize * sizeof(float);
        public override object ReadValueAsObject()
        {
            var r = new float[m_StateSize];
            Array.Copy(m_State, r, m_StateSize);
            return r;
        }

        /// <summary>이 장치를 같은 종류의 current 로 (Gamepad.current …)</summary>
        public virtual void MakeCurrent() { }

        protected virtual void OnAdded() { }
        protected virtual void OnRemoved() { }
        internal void CallAdded() => OnAdded();
        internal void CallRemoved() => OnRemoved();

        internal bool HasLayout(string layout)
        {
            foreach (string l in m_LayoutChain)
                if (string.Equals(l, layout, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- 상태 칸
        internal float Slot(int slot, bool previous) => (previous ? m_Prev : m_State)[slot];
        internal void SetSlot(int slot, float value) { m_State[slot] = value; m_Dirty = true; }
        internal void BeginFrame() => Array.Copy(m_State, m_Prev, m_StateSize);
        internal bool StateChanged()
        {
            for (int i = 0; i < m_StateSize; i++)
                if (m_State[i] != m_Prev[i]) return true;
            return false;
        }

        internal int Alloc()
        {
            if (m_StateSize == m_State.Length)
            {
                Array.Resize(ref m_State, m_State.Length * 2);
                Array.Resize(ref m_Prev, m_Prev.Length * 2);
            }
            return m_StateSize++;
        }

        internal void SetName(string name)
        {
            m_Name = name;
            InvalidatePath();
        }

        // ---- 레이아웃 만들기 (장치 생성자에서)
        internal T Add<T>(InputControl parent, T c, string name, string layout, string display = null, string shortDisplay = null) where T : InputControl
        {
            c.m_Name = name;
            c.m_Layout = layout;
            c.m_DisplayName = display;
            c.m_ShortDisplayName = shortDisplay;
            c.m_Device = this;
            c.m_Parent = parent ?? this;
            c.m_Parent.AddChild(c);
            m_All.Add(c);
            m_AllArray = null;
            return c;
        }

        internal ButtonControl Button(InputControl parent, string name, string display, string shortDisplay = null)
        {
            var b = Add(parent, new ButtonControl(), name, "Button", display, shortDisplay);
            b.m_Slot = Alloc();
            return b;
        }

        internal AxisControl Axis(InputControl parent, string name, string display)
        {
            var a = Add(parent, new AxisControl(), name, "Axis", display);
            a.m_Slot = Alloc();
            return a;
        }

        internal IntegerControl Integer(InputControl parent, string name, string display)
        {
            var c = Add(parent, new IntegerControl(), name, "Integer", display);
            c.m_Slot = Alloc();
            return c;
        }

        // x · y 칸이 있는 Vector2
        internal T Vec2<T>(InputControl parent, T v, string name, string layout, string display, string shortDisplay = null) where T : Vector2Control
        {
            Add(parent, v, name, layout, display, shortDisplay);
            v.x = Axis(v, "x", "X");
            v.y = Axis(v, "y", "Y");
            return v;
        }

        // 한 축의 한쪽 (스틱 up = y 의 0 ~ 1, down = y 의 -1 ~ 0 을 뒤집어)
        internal ButtonControl HalfAxis(InputControl parent, AxisControl source, string name, string display, bool negative)
        {
            var b = Add(parent, new ButtonControl(), name, "Button", display);
            b.m_Slot = source.m_Slot;
            b.clamp = AxisControl.Clamp.AfterNormalize;
            b.clampMin = negative ? -1f : 0f;
            b.clampMax = negative ? 0f : 1f;
            b.invert = negative;
            b.m_Synthetic = true;
            return b;
        }

        internal void Usages(InputControl c, params string[] usages) => c.m_Usages = usages;
        internal void Aliases(InputControl c, params string[] aliases) => c.m_Aliases = aliases;
    }

    // ------------------------------------------------------------------ Keyboard

    /// <summary>Unity 의 Key (InputSystem) — 값도 Unity 와 같다</summary>
    public enum Key
    {
        None = 0, Space, Enter, Tab, Backquote, Quote, Semicolon, Comma, Period, Slash, Backslash, LeftBracket, RightBracket, Minus, Equals,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Digit0,
        LeftShift, RightShift, LeftAlt, RightAlt, AltGr = RightAlt, LeftCtrl, RightCtrl,
        LeftMeta, RightMeta, LeftWindows = LeftMeta, RightWindows = RightMeta, LeftApple = LeftMeta, RightApple = RightMeta, LeftCommand = LeftMeta, RightCommand = RightMeta,
        ContextMenu, Escape, LeftArrow, RightArrow, UpArrow, DownArrow, Backspace, PageDown, PageUp, Home, End, Insert, Delete, CapsLock, NumLock, PrintScreen, ScrollLock, Pause,
        NumpadEnter, NumpadDivide, NumpadMultiply, NumpadPlus, NumpadMinus, NumpadPeriod, NumpadEquals,
        Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        OEM1, OEM2, OEM3, OEM4, OEM5, IMESelected,
    }

    /// <summary>Unity 의 Keyboard — 키마다 KeyControl ("&lt;Keyboard&gt;/w", "&lt;Keyboard&gt;/space"), anyKey · shift · ctrl · alt</summary>
    public class Keyboard : InputDevice
    {
        public const int KeyCount = (int)Key.OEM5;

        internal struct KeyInfo { public Key Key; public string Name, Display; public int Vk; public string[] Aliases; }
        internal static readonly KeyInfo[] s_Keys = BuildKeyTable();

        readonly KeyControl[] m_Keys = new KeyControl[KeyCount];

        public static Keyboard current { get; internal set; }

        public Keyboard() : this("Keyboard") { }
        internal Keyboard(params string[] chain) : base(chain)
        {
            m_Description = new InputDeviceDescription { interfaceName = "Win32", deviceClass = "Keyboard", product = "Keyboard" };
            foreach (KeyInfo k in s_Keys)
            {
                var c = Add(null, new KeyControl(), k.Name, "Key", k.Display);
                c.m_Slot = Alloc();
                c.m_Key = k.Key;
                c.m_VirtualKey = k.Vk;
                if (k.Aliases != null) c.m_Aliases = k.Aliases;
                m_Keys[(int)k.Key - 1] = c;
            }
            Usages(m_Keys[(int)Key.Enter - 1], "Submit");
            Usages(m_Keys[(int)Key.Escape - 1], "Back", "Cancel");
            anyKey = Add(null, new AnyKeyControl(), "anyKey", "AnyKey", "Any Key");
            anyKey.m_Synthetic = true;
            anyKey.m_Reader = prev => { foreach (var c in m_Keys) if (c != null && c.ReadSlot(prev) >= 0.5f) return 1f; return 0f; };
            shiftKey = Either("shift", "Shift", Key.LeftShift, Key.RightShift);
            ctrlKey = Either("ctrl", "Control", Key.LeftCtrl, Key.RightCtrl);
            altKey = Either("alt", "Alt", Key.LeftAlt, Key.RightAlt);
            imeSelected = Button(null, "imeSelected", "IME Selected");
            imeSelected.m_Synthetic = true;
        }

        ButtonControl Either(string name, string display, Key a, Key b)
        {
            var ka = m_Keys[(int)a - 1];
            var kb = m_Keys[(int)b - 1];
            var c = Add(null, new ButtonControl(), name, "DiscreteButton", display);
            c.m_Synthetic = true;
            c.m_Reader = prev => Math.Max(ka.ReadSlot(prev), kb.ReadSlot(prev));
            return c;
        }

        public KeyControl this[Key key]
        {
            get
            {
                int i = (int)key - 1;
                if (i < 0 || i >= m_Keys.Length || m_Keys[i] == null) throw new ArgumentOutOfRangeException(nameof(key));
                return m_Keys[i];
            }
        }

        public ReadOnlyArray<KeyControl> allKeys => m_Keys;
        public AnyKeyControl anyKey { get; }
        public ButtonControl shiftKey { get; }
        public ButtonControl ctrlKey { get; }
        public ButtonControl altKey { get; }
        public ButtonControl imeSelected { get; }
        public string keyboardLayout => "en-US";

        /// <summary>글자 입력 (Unity 의 onTextInput) — 아직 엔진이 넘기지 않는다</summary>
        public event Action<char> onTextInput { add { } remove { } }

        public override void MakeCurrent() => current = this;
        protected override void OnRemoved() { if (current == this) current = null; }

        public KeyControl spaceKey => this[Key.Space];
        public KeyControl enterKey => this[Key.Enter];
        public KeyControl tabKey => this[Key.Tab];
        public KeyControl backquoteKey => this[Key.Backquote];
        public KeyControl quoteKey => this[Key.Quote];
        public KeyControl semicolonKey => this[Key.Semicolon];
        public KeyControl commaKey => this[Key.Comma];
        public KeyControl periodKey => this[Key.Period];
        public KeyControl slashKey => this[Key.Slash];
        public KeyControl backslashKey => this[Key.Backslash];
        public KeyControl leftBracketKey => this[Key.LeftBracket];
        public KeyControl rightBracketKey => this[Key.RightBracket];
        public KeyControl minusKey => this[Key.Minus];
        public KeyControl equalsKey => this[Key.Equals];
        public KeyControl aKey => this[Key.A];
        public KeyControl bKey => this[Key.B];
        public KeyControl cKey => this[Key.C];
        public KeyControl dKey => this[Key.D];
        public KeyControl eKey => this[Key.E];
        public KeyControl fKey => this[Key.F];
        public KeyControl gKey => this[Key.G];
        public KeyControl hKey => this[Key.H];
        public KeyControl iKey => this[Key.I];
        public KeyControl jKey => this[Key.J];
        public KeyControl kKey => this[Key.K];
        public KeyControl lKey => this[Key.L];
        public KeyControl mKey => this[Key.M];
        public KeyControl nKey => this[Key.N];
        public KeyControl oKey => this[Key.O];
        public KeyControl pKey => this[Key.P];
        public KeyControl qKey => this[Key.Q];
        public KeyControl rKey => this[Key.R];
        public KeyControl sKey => this[Key.S];
        public KeyControl tKey => this[Key.T];
        public KeyControl uKey => this[Key.U];
        public KeyControl vKey => this[Key.V];
        public KeyControl wKey => this[Key.W];
        public KeyControl xKey => this[Key.X];
        public KeyControl yKey => this[Key.Y];
        public KeyControl zKey => this[Key.Z];
        public KeyControl digit1Key => this[Key.Digit1];
        public KeyControl digit2Key => this[Key.Digit2];
        public KeyControl digit3Key => this[Key.Digit3];
        public KeyControl digit4Key => this[Key.Digit4];
        public KeyControl digit5Key => this[Key.Digit5];
        public KeyControl digit6Key => this[Key.Digit6];
        public KeyControl digit7Key => this[Key.Digit7];
        public KeyControl digit8Key => this[Key.Digit8];
        public KeyControl digit9Key => this[Key.Digit9];
        public KeyControl digit0Key => this[Key.Digit0];
        public KeyControl leftShiftKey => this[Key.LeftShift];
        public KeyControl rightShiftKey => this[Key.RightShift];
        public KeyControl leftAltKey => this[Key.LeftAlt];
        public KeyControl rightAltKey => this[Key.RightAlt];
        public KeyControl leftCtrlKey => this[Key.LeftCtrl];
        public KeyControl rightCtrlKey => this[Key.RightCtrl];
        public KeyControl leftMetaKey => this[Key.LeftMeta];
        public KeyControl rightMetaKey => this[Key.RightMeta];
        public KeyControl leftWindowsKey => this[Key.LeftWindows];
        public KeyControl rightWindowsKey => this[Key.RightWindows];
        public KeyControl leftAppleKey => this[Key.LeftApple];
        public KeyControl rightAppleKey => this[Key.RightApple];
        public KeyControl leftCommandKey => this[Key.LeftCommand];
        public KeyControl rightCommandKey => this[Key.RightCommand];
        public KeyControl contextMenuKey => this[Key.ContextMenu];
        public KeyControl escapeKey => this[Key.Escape];
        public KeyControl leftArrowKey => this[Key.LeftArrow];
        public KeyControl rightArrowKey => this[Key.RightArrow];
        public KeyControl upArrowKey => this[Key.UpArrow];
        public KeyControl downArrowKey => this[Key.DownArrow];
        public KeyControl backspaceKey => this[Key.Backspace];
        public KeyControl pageDownKey => this[Key.PageDown];
        public KeyControl pageUpKey => this[Key.PageUp];
        public KeyControl homeKey => this[Key.Home];
        public KeyControl endKey => this[Key.End];
        public KeyControl insertKey => this[Key.Insert];
        public KeyControl deleteKey => this[Key.Delete];
        public KeyControl capsLockKey => this[Key.CapsLock];
        public KeyControl numLockKey => this[Key.NumLock];
        public KeyControl printScreenKey => this[Key.PrintScreen];
        public KeyControl scrollLockKey => this[Key.ScrollLock];
        public KeyControl pauseKey => this[Key.Pause];
        public KeyControl numpadEnterKey => this[Key.NumpadEnter];
        public KeyControl numpadDivideKey => this[Key.NumpadDivide];
        public KeyControl numpadMultiplyKey => this[Key.NumpadMultiply];
        public KeyControl numpadPlusKey => this[Key.NumpadPlus];
        public KeyControl numpadMinusKey => this[Key.NumpadMinus];
        public KeyControl numpadPeriodKey => this[Key.NumpadPeriod];
        public KeyControl numpadEqualsKey => this[Key.NumpadEquals];
        public KeyControl numpad0Key => this[Key.Numpad0];
        public KeyControl numpad1Key => this[Key.Numpad1];
        public KeyControl numpad2Key => this[Key.Numpad2];
        public KeyControl numpad3Key => this[Key.Numpad3];
        public KeyControl numpad4Key => this[Key.Numpad4];
        public KeyControl numpad5Key => this[Key.Numpad5];
        public KeyControl numpad6Key => this[Key.Numpad6];
        public KeyControl numpad7Key => this[Key.Numpad7];
        public KeyControl numpad8Key => this[Key.Numpad8];
        public KeyControl numpad9Key => this[Key.Numpad9];
        public KeyControl f1Key => this[Key.F1];
        public KeyControl f2Key => this[Key.F2];
        public KeyControl f3Key => this[Key.F3];
        public KeyControl f4Key => this[Key.F4];
        public KeyControl f5Key => this[Key.F5];
        public KeyControl f6Key => this[Key.F6];
        public KeyControl f7Key => this[Key.F7];
        public KeyControl f8Key => this[Key.F8];
        public KeyControl f9Key => this[Key.F9];
        public KeyControl f10Key => this[Key.F10];
        public KeyControl f11Key => this[Key.F11];
        public KeyControl f12Key => this[Key.F12];
        public KeyControl oem1Key => this[Key.OEM1];
        public KeyControl oem2Key => this[Key.OEM2];
        public KeyControl oem3Key => this[Key.OEM3];
        public KeyControl oem4Key => this[Key.OEM4];
        public KeyControl oem5Key => this[Key.OEM5];

        static KeyInfo[] BuildKeyTable()
        {
            var l = new List<KeyInfo>();
            void K(Key k, string name, string display, int vk, params string[] aliases) => l.Add(new KeyInfo { Key = k, Name = name, Display = display, Vk = vk, Aliases = aliases.Length > 0 ? aliases : null });
            K(Key.Space, "space", "Space", 0x20); K(Key.Enter, "enter", "Enter", 0x0D); K(Key.Tab, "tab", "Tab", 0x09);
            K(Key.Backquote, "backquote", "`", 0xC0); K(Key.Quote, "quote", "'", 0xDE); K(Key.Semicolon, "semicolon", ";", 0xBA);
            K(Key.Comma, "comma", ",", 0xBC); K(Key.Period, "period", ".", 0xBE); K(Key.Slash, "slash", "/", 0xBF);
            K(Key.Backslash, "backslash", "\\", 0xDC); K(Key.LeftBracket, "leftBracket", "[", 0xDB); K(Key.RightBracket, "rightBracket", "]", 0xDD);
            K(Key.Minus, "minus", "-", 0xBD); K(Key.Equals, "equals", "=", 0xBB);
            for (Key k = Key.A; k <= Key.Z; k++) { char c = (char)('a' + (k - Key.A)); K(k, c.ToString(), char.ToUpperInvariant(c).ToString(), 'A' + (k - Key.A)); }
            for (Key k = Key.Digit1; k <= Key.Digit9; k++) { int n = k - Key.Digit1 + 1; K(k, n.ToString(), n.ToString(), '0' + n); }
            K(Key.Digit0, "0", "0", '0');
            K(Key.LeftShift, "leftShift", "Left Shift", 0xA0); K(Key.RightShift, "rightShift", "Right Shift", 0xA1);
            K(Key.LeftAlt, "leftAlt", "Left Alt", 0xA4); K(Key.RightAlt, "rightAlt", "Right Alt", 0xA5, "AltGr");
            K(Key.LeftCtrl, "leftCtrl", "Left Control", 0xA2); K(Key.RightCtrl, "rightCtrl", "Right Control", 0xA3);
            K(Key.LeftMeta, "leftMeta", "Left System", 0x5B, "leftWindows", "leftApple", "leftCommand");
            K(Key.RightMeta, "rightMeta", "Right System", 0x5C, "rightWindows", "rightApple", "rightCommand");
            K(Key.ContextMenu, "contextMenu", "Context Menu", 0x5D); K(Key.Escape, "escape", "Escape", 0x1B);
            K(Key.LeftArrow, "leftArrow", "Left Arrow", 0x25); K(Key.RightArrow, "rightArrow", "Right Arrow", 0x27);
            K(Key.UpArrow, "upArrow", "Up Arrow", 0x26); K(Key.DownArrow, "downArrow", "Down Arrow", 0x28);
            K(Key.Backspace, "backspace", "Backspace", 0x08); K(Key.PageDown, "pageDown", "Page Down", 0x22); K(Key.PageUp, "pageUp", "Page Up", 0x21);
            K(Key.Home, "home", "Home", 0x24); K(Key.End, "end", "End", 0x23); K(Key.Insert, "insert", "Insert", 0x2D); K(Key.Delete, "delete", "Delete", 0x2E);
            K(Key.CapsLock, "capsLock", "Caps Lock", 0x14); K(Key.NumLock, "numLock", "Num Lock", 0x90); K(Key.PrintScreen, "printScreen", "Print Screen", 0x2C);
            K(Key.ScrollLock, "scrollLock", "Scroll Lock", 0x91); K(Key.Pause, "pause", "Pause/Break", 0x13);
            K(Key.NumpadEnter, "numpadEnter", "Numpad Enter", 0); K(Key.NumpadDivide, "numpadDivide", "Numpad /", 0x6F);
            K(Key.NumpadMultiply, "numpadMultiply", "Numpad *", 0x6A); K(Key.NumpadPlus, "numpadPlus", "Numpad +", 0x6B);
            K(Key.NumpadMinus, "numpadMinus", "Numpad -", 0x6D); K(Key.NumpadPeriod, "numpadPeriod", "Numpad .", 0x6E); K(Key.NumpadEquals, "numpadEquals", "Numpad =", 0x92);
            for (Key k = Key.Numpad0; k <= Key.Numpad9; k++) { int n = k - Key.Numpad0; K(k, "numpad" + n, "Numpad " + n, 0x60 + n); }
            for (Key k = Key.F1; k <= Key.F12; k++) { int n = k - Key.F1 + 1; K(k, "f" + n, "F" + n, 0x70 + n - 1); }
            K(Key.OEM1, "OEM1", "OEM1", 0xE2); K(Key.OEM2, "OEM2", "OEM2", 0); K(Key.OEM3, "OEM3", "OEM3", 0); K(Key.OEM4, "OEM4", "OEM4", 0); K(Key.OEM5, "OEM5", "OEM5", 0);
            return l.ToArray();
        }
    }

    // ------------------------------------------------------------------ Pointer · Mouse

    /// <summary>Unity 의 Pointer — 마우스 · 터치 화면의 공통 (position · delta · press)</summary>
    public class Pointer : InputDevice
    {
        public static Pointer current { get; internal set; }

        public Vector2Control position { get; protected set; }
        public DeltaControl delta { get; protected set; }
        public ButtonControl press { get; protected set; }
        public AxisControl pressure { get; protected set; }

        public Pointer() : this("Pointer") { }
        internal Pointer(params string[] chain) : base(chain) { }

        public override void MakeCurrent() => current = this;
        protected override void OnRemoved() { if (current == this) current = null; }

        internal DeltaControl MakeDelta(string name, string display)
        {
            var d = Vec2(null, new DeltaControl(), name, "Delta", display);
            d.up = HalfAxis(d, d.y, "up", "Up", false);
            d.down = HalfAxis(d, d.y, "down", "Down", true);
            d.left = HalfAxis(d, d.x, "left", "Left", true);
            d.right = HalfAxis(d, d.x, "right", "Right", false);
            return d;
        }
    }

    /// <summary>Unity 의 Mouse — position (게임 화면 픽셀, 왼쪽 아래 0,0) · delta · scroll (눈금 하나 = 1) · 단추 다섯</summary>
    public class Mouse : Pointer
    {
        public static new Mouse current { get; internal set; }

        public DeltaControl scroll { get; }
        public ButtonControl leftButton { get; }
        public ButtonControl rightButton { get; }
        public ButtonControl middleButton { get; }
        public ButtonControl forwardButton { get; }
        public ButtonControl backButton { get; }
        public IntegerControl clickCount { get; }

        public Mouse() : this("Mouse", "Pointer") { }
        internal Mouse(params string[] chain) : base(chain)
        {
            m_Description = new InputDeviceDescription { interfaceName = "Win32", deviceClass = "Mouse", product = "Mouse" };
            position = Vec2(null, new Vector2Control(), "position", "Vector2", "Position");
            position.m_Noisy = true;
            Usages(position, "Point");
            delta = MakeDelta("delta", "Delta");
            delta.m_Noisy = true;
            Usages(delta, "Secondary2DMotion");
            scroll = MakeDelta("scroll", "Scroll");
            Usages(scroll, "ScrollHorizontal", "ScrollVertical");
            leftButton = Button(null, "leftButton", "Left Button", "LMB");
            Usages(leftButton, "PrimaryAction");
            rightButton = Button(null, "rightButton", "Right Button", "RMB");
            Usages(rightButton, "SecondaryAction");
            middleButton = Button(null, "middleButton", "Middle Button", "MMB");
            forwardButton = Button(null, "forwardButton", "Forward");
            Usages(forwardButton, "Forward");
            backButton = Button(null, "backButton", "Back");
            Usages(backButton, "Back");
            var lb = leftButton;
            press = Add(null, new ButtonControl(), "press", "Button", "Press");
            press.m_Synthetic = true;
            press.m_Reader = prev => lb.ReadSlot(prev);
            pressure = Axis(null, "pressure", "Pressure");
            clickCount = Integer(null, "clickCount", "Click Count");
        }

        public override void MakeCurrent() { base.MakeCurrent(); current = this; }
        protected override void OnRemoved() { base.OnRemoved(); if (current == this) current = null; }

        /// <summary>커서를 게임 화면 좌표 (왼쪽 아래 0,0) 로 옮긴다</summary>
        public void WarpCursorPosition(Vector2 position) => InputNative.NovaInputSys_WarpCursor(position.x, position.y);
    }

    // ------------------------------------------------------------------ Gamepad

    /// <summary>Unity 의 GamepadButton (Gamepad[GamepadButton.South])</summary>
    public enum GamepadButton
    {
        DpadUp = 0, DpadDown = 1, DpadLeft = 2, DpadRight = 3,
        North = 4, East = 5, South = 6, West = 7,
        LeftStick = 8, RightStick = 9, LeftShoulder = 10, RightShoulder = 11, Start = 12, Select = 13,
        LeftTrigger = 32, RightTrigger = 33,
        X = West, Y = North, A = South, B = East,
        Cross = South, Square = West, Triangle = North, Circle = East,
    }

    /// <summary>
    /// Unity 의 Gamepad — 위치로 이름 붙인 단추 (buttonSouth = Xbox A · PlayStation ×), 스틱 둘 (기본 stickDeadzone),
    /// 트리거 둘, 십자키. Windows 는 XInput (Xbox 호환 패드 4 개까지), SetMotorSpeeds 진동.
    /// </summary>
    public class Gamepad : InputDevice
    {
        static readonly List<Gamepad> s_All = new List<Gamepad>();
        public static Gamepad current { get; internal set; }
        public static ReadOnlyArray<Gamepad> all => s_All.ToArray();

        public DpadControl dpad { get; }
        public ButtonControl buttonSouth { get; }
        public ButtonControl buttonWest { get; }
        public ButtonControl buttonNorth { get; }
        public ButtonControl buttonEast { get; }
        public ButtonControl leftStickButton { get; }
        public ButtonControl rightStickButton { get; }
        public ButtonControl startButton { get; }
        public ButtonControl selectButton { get; }
        public ButtonControl leftShoulder { get; }
        public ButtonControl rightShoulder { get; }
        public ButtonControl leftTrigger { get; }
        public ButtonControl rightTrigger { get; }
        public StickControl leftStick { get; }
        public StickControl rightStick { get; }

        public ButtonControl aButton => buttonSouth;
        public ButtonControl bButton => buttonEast;
        public ButtonControl xButton => buttonWest;
        public ButtonControl yButton => buttonNorth;
        public ButtonControl crossButton => buttonSouth;
        public ButtonControl circleButton => buttonEast;
        public ButtonControl squareButton => buttonWest;
        public ButtonControl triangleButton => buttonNorth;

        public Gamepad() : this("Gamepad") { }
        internal Gamepad(params string[] chain) : base(chain)
        {
            m_Description = new InputDeviceDescription { interfaceName = "Virtual", deviceClass = "Gamepad", product = "Gamepad" };
            dpad = Add(null, new DpadControl(), "dpad", "Dpad", "D-Pad");
            Usages(dpad, "Hatswitch");
            dpad.up = Button(dpad, "up", "Up");
            dpad.down = Button(dpad, "down", "Down");
            dpad.left = Button(dpad, "left", "Left");
            dpad.right = Button(dpad, "right", "Right");
            var d = dpad;
            dpad.x = Add(dpad, new AxisControl(), "x", "DpadAxis", "X");
            dpad.x.m_Synthetic = true;
            dpad.x.m_Reader = prev => d.right.ReadSlot(prev) - d.left.ReadSlot(prev);
            dpad.y = Add(dpad, new AxisControl(), "y", "DpadAxis", "Y");
            dpad.y.m_Synthetic = true;
            dpad.y.m_Reader = prev => d.up.ReadSlot(prev) - d.down.ReadSlot(prev);

            buttonSouth = Button(null, "buttonSouth", "Button South", "A");
            Aliases(buttonSouth, "a", "cross");
            Usages(buttonSouth, "PrimaryAction", "Submit");
            buttonWest = Button(null, "buttonWest", "Button West", "X");
            Aliases(buttonWest, "x", "square");
            Usages(buttonWest, "SecondaryAction");
            buttonNorth = Button(null, "buttonNorth", "Button North", "Y");
            Aliases(buttonNorth, "y", "triangle");
            buttonEast = Button(null, "buttonEast", "Button East", "B");
            Aliases(buttonEast, "b", "circle");
            Usages(buttonEast, "Back", "Cancel");
            leftStickButton = Button(null, "leftStickPress", "Left Stick Press", "LS");
            rightStickButton = Button(null, "rightStickPress", "Right Stick Press", "RS");
            leftShoulder = Button(null, "leftShoulder", "Left Shoulder", "LB");
            rightShoulder = Button(null, "rightShoulder", "Right Shoulder", "RB");
            startButton = Button(null, "start", "Start");
            Usages(startButton, "Menu");
            selectButton = Button(null, "select", "Select");
            leftStick = Stick("leftStick", "Left Stick", "LS");
            Usages(leftStick, "Primary2DMotion");
            rightStick = Stick("rightStick", "Right Stick", "RS");
            Usages(rightStick, "Secondary2DMotion");
            leftTrigger = Button(null, "leftTrigger", "Left Trigger", "LT");
            Usages(leftTrigger, "SecondaryTrigger");
            rightTrigger = Button(null, "rightTrigger", "Right Trigger", "RT");
            Usages(rightTrigger, "PrimaryTrigger");
        }

        StickControl Stick(string name, string display, string shortDisplay)
        {
            var s = Vec2(null, new StickControl(), name, "Stick", display, shortDisplay);
            s.AddProcessor(new StickDeadzoneProcessor());
            s.x.AddProcessor(new AxisDeadzoneProcessor());
            s.y.AddProcessor(new AxisDeadzoneProcessor());
            s.up = HalfAxis(s, s.y, "up", "Up", false);
            s.down = HalfAxis(s, s.y, "down", "Down", true);
            s.left = HalfAxis(s, s.x, "left", "Left", true);
            s.right = HalfAxis(s, s.x, "right", "Right", false);
            foreach (var b in new[] { s.up, s.down, s.left, s.right }) b.AddProcessor(new AxisDeadzoneProcessor());
            return s;
        }

        public ButtonControl this[GamepadButton button]
        {
            get
            {
                switch (button)
                {
                    case GamepadButton.North: return buttonNorth;
                    case GamepadButton.South: return buttonSouth;
                    case GamepadButton.East: return buttonEast;
                    case GamepadButton.West: return buttonWest;
                    case GamepadButton.DpadUp: return dpad.up;
                    case GamepadButton.DpadDown: return dpad.down;
                    case GamepadButton.DpadLeft: return dpad.left;
                    case GamepadButton.DpadRight: return dpad.right;
                    case GamepadButton.LeftShoulder: return leftShoulder;
                    case GamepadButton.RightShoulder: return rightShoulder;
                    case GamepadButton.LeftTrigger: return leftTrigger;
                    case GamepadButton.RightTrigger: return rightTrigger;
                    case GamepadButton.Start: return startButton;
                    case GamepadButton.Select: return selectButton;
                    case GamepadButton.LeftStick: return leftStickButton;
                    case GamepadButton.RightStick: return rightStickButton;
                }
                throw new ArgumentOutOfRangeException(nameof(button));
            }
        }

        public override void MakeCurrent() => current = this;
        protected override void OnAdded() { s_All.Add(this); }
        protected override void OnRemoved()
        {
            s_All.Remove(this);
            if (current == this) current = s_All.Count > 0 ? s_All[s_All.Count - 1] : null;
        }

        // ---- 진동 (Unity 의 IDualMotorRumble): 낮은 쪽 = 왼쪽 큰 모터, 높은 쪽 = 오른쪽 작은 모터 (0 ~ 1)
        float m_Low, m_High;
        bool m_HapticsPaused;
        public virtual void SetMotorSpeeds(float lowFrequency, float highFrequency)
        {
            m_Low = Math.Clamp(lowFrequency, 0f, 1f);
            m_High = Math.Clamp(highFrequency, 0f, 1f);
            if (!m_HapticsPaused) SendMotors(m_Low, m_High);
        }
        public virtual void PauseHaptics() { m_HapticsPaused = true; SendMotors(0, 0); }
        public virtual void ResumeHaptics() { m_HapticsPaused = false; SendMotors(m_Low, m_High); }
        public virtual void ResetHaptics() { m_HapticsPaused = false; m_Low = m_High = 0; SendMotors(0, 0); }
        internal void SendMotors(float low, float high)
        {
            if (m_NativeSlot >= 0) InputNative.NovaInputSys_SetMotors(m_NativeSlot, low, high);
        }
        internal float motorLow => m_Low;
        internal float motorHigh => m_High;
    }

    // ------------------------------------------------------------------ Touchscreen

    /// <summary>Unity 의 Touchscreen — primaryTouch · touch0 ~ touch9 (안드로이드). position · press · delta 는 첫 손가락</summary>
    public class Touchscreen : Pointer
    {
        public const int MaxTouches = 10;
        public static new Touchscreen current { get; internal set; }

        public TouchControl primaryTouch { get; }
        readonly TouchControl[] m_Touches = new TouchControl[MaxTouches];
        public ReadOnlyArray<TouchControl> touches => m_Touches;

        public Touchscreen() : this("Touchscreen", "Pointer") { }
        internal Touchscreen(params string[] chain) : base(chain)
        {
            m_Description = new InputDeviceDescription { interfaceName = "Android", deviceClass = "Touchscreen", product = "Touchscreen" };
            primaryTouch = MakeTouch("primaryTouch", "Primary Touch");
            for (int i = 0; i < MaxTouches; i++) m_Touches[i] = MakeTouch("touch" + i, "Touch #" + i);
            var p = primaryTouch;
            position = Add(null, new Vector2Control(), "position", "Vector2", "Position");
            position.x = Add(position, new AxisControl(), "x", "Axis", "X");
            position.x.m_Reader = prev => p.position.x.ReadSlot(prev);
            position.y = Add(position, new AxisControl(), "y", "Axis", "Y");
            position.y.m_Reader = prev => p.position.y.ReadSlot(prev);
            position.m_Synthetic = true;
            position.m_Noisy = true;
            Usages(position, "Point");
            delta = Add(null, new DeltaControl(), "delta", "Delta", "Delta");
            delta.x = Add(delta, new AxisControl(), "x", "Axis", "X");
            delta.x.m_Reader = prev => p.delta.x.ReadSlot(prev);
            delta.y = Add(delta, new AxisControl(), "y", "Axis", "Y");
            delta.y.m_Reader = prev => p.delta.y.ReadSlot(prev);
            delta.up = HalfAxis(delta, delta.y, "up", "Up", false); delta.up.m_Reader = delta.y.m_Reader;
            delta.down = HalfAxis(delta, delta.y, "down", "Down", true); delta.down.m_Reader = delta.y.m_Reader;
            delta.left = HalfAxis(delta, delta.x, "left", "Left", true); delta.left.m_Reader = delta.x.m_Reader;
            delta.right = HalfAxis(delta, delta.x, "right", "Right", false); delta.right.m_Reader = delta.x.m_Reader;
            delta.m_Synthetic = true;
            delta.m_Noisy = true;
            press = Add(null, new ButtonControl(), "press", "Button", "Press");
            press.m_Reader = prev => p.press.ReadSlot(prev);
            press.m_Synthetic = true;
            pressure = Add(null, new AxisControl(), "pressure", "Axis", "Pressure");
            pressure.m_Reader = prev => p.pressure.ReadSlot(prev);
        }

        TouchControl MakeTouch(string name, string display)
        {
            var t = Add(null, new TouchControl(), name, "Touch", display);
            t.press = Add(t, new TouchPressControl(), "press", "TouchPress", "Press");
            t.press.m_Slot = Alloc();
            t.touchId = Integer(t, "touchId", "Touch ID");
            t.position = Vec2(t, new Vector2Control(), "position", "Vector2", "Position");
            t.position.m_Noisy = true;
            t.delta = Vec2(t, new DeltaControl(), "delta", "Delta", "Delta");
            t.delta.m_Noisy = true;
            t.delta.up = HalfAxis(t.delta, t.delta.y, "up", "Up", false);
            t.delta.down = HalfAxis(t.delta, t.delta.y, "down", "Down", true);
            t.delta.left = HalfAxis(t.delta, t.delta.x, "left", "Left", true);
            t.delta.right = HalfAxis(t.delta, t.delta.x, "right", "Right", false);
            t.pressure = Axis(t, "pressure", "Pressure");
            t.radius = Vec2(t, new Vector2Control(), "radius", "Vector2", "Radius");
            t.phase = Add(t, new TouchPhaseControl(), "phase", "TouchPhase", "Phase");
            t.phase.m_Slot = Alloc();
            t.tap = Button(t, "tap", "Tap");
            t.tapCount = Integer(t, "tapCount", "Tap Count");
            t.startPosition = Vec2(t, new Vector2Control(), "startPosition", "Vector2", "Start Position");
            t.startTime = Add(t, new DoubleControl(), "startTime", "Double", "Start Time");
            t.startTime.m_Slot = Alloc();
            return t;
        }

        public override void MakeCurrent() { base.MakeCurrent(); current = this; }
        protected override void OnRemoved() { base.OnRemoved(); if (current == this) current = null; }
    }
}

namespace NovaEngine.InputSystem.XInput
{
    /// <summary>Unity 의 XInputController — Xbox 호환 패드 (A · B · X · Y, View · Menu)</summary>
    public class XInputController : Gamepad
    {
        public ButtonControl menu => startButton;
        public ButtonControl view => selectButton;

        public XInputController() : this("XInputController", "Gamepad") { }
        internal XInputController(params string[] chain) : base(chain)
        {
            m_Description = new InputDeviceDescription { interfaceName = "XInput", deviceClass = "Gamepad", product = "Xbox Controller" };
            buttonSouth.m_DisplayName = "A";
            buttonEast.m_DisplayName = "B";
            buttonWest.m_DisplayName = "X";
            buttonNorth.m_DisplayName = "Y";
            leftShoulder.m_DisplayName = "Left Bumper";
            rightShoulder.m_DisplayName = "Right Bumper";
            startButton.m_DisplayName = "Menu";
            selectButton.m_DisplayName = "View";
        }
    }

    /// <summary>Windows XInput 패드 (Unity 와 같은 레이아웃 이름)</summary>
    public class XInputControllerWindows : XInputController
    {
        public XInputControllerWindows() : base("XInputControllerWindows", "XInputController", "Gamepad") { }
    }
}
