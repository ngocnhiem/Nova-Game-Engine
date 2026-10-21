using System;
using System.Collections.Generic;
using NovaEngine.InputSystem.Controls;

namespace NovaEngine.InputSystem.LowLevel
{
    /// <summary>상태 형식 이름 (Unity 의 FourCC — 'KEYS', 'MOUS', 'GPAD', 'TOUC')</summary>
    public readonly struct FourCC : IEquatable<FourCC>
    {
        readonly int m_Code;
        public FourCC(int code) { m_Code = code; }
        public FourCC(char a, char b = ' ', char c = ' ', char d = ' ') { m_Code = (a << 24) | (b << 16) | (c << 8) | d; }
        public FourCC(string str) : this(str.Length > 0 ? str[0] : ' ', str.Length > 1 ? str[1] : ' ', str.Length > 2 ? str[2] : ' ', str.Length > 3 ? str[3] : ' ') { }
        public static implicit operator int(FourCC f) => f.m_Code;
        public static implicit operator FourCC(int i) => new FourCC(i);
        public bool Equals(FourCC other) => m_Code == other.m_Code;
        public override bool Equals(object obj) => obj is FourCC f && Equals(f);
        public override int GetHashCode() => m_Code;
        public override string ToString() => new string(new[] { (char)(m_Code >> 24), (char)((m_Code >> 16) & 0xff), (char)((m_Code >> 8) & 0xff), (char)(m_Code & 0xff) });
    }

    /// <summary>InputSystem.QueueStateEvent 에 넘기는 상태 (Unity 와 같은 이름)</summary>
    public interface IInputStateTypeInfo
    {
        FourCC format { get; }
    }

    // 상태를 장치 칸에 쓴다 (NOVA 내부)
    internal interface IStateWriter
    {
        bool CanWrite(InputDevice device);
        void Write(InputDevice device);
    }

    /// <summary>Unity 의 KeyboardState — 눌린 키 목록</summary>
    public struct KeyboardState : IInputStateTypeInfo, IStateWriter
    {
        ulong m_Bits0, m_Bits1;
        public static FourCC Format => new FourCC('K', 'E', 'Y', 'S');
        public FourCC format => Format;

        public KeyboardState(params Key[] pressedKeys)
        {
            m_Bits0 = m_Bits1 = 0;
            if (pressedKeys != null)
                foreach (Key k in pressedKeys) Set(k, true);
        }

        public void Set(Key key, bool state)
        {
            int i = (int)key;
            if (i <= 0 || i >= 128) return;
            ulong bit = 1UL << (i & 63);
            if (i < 64) m_Bits0 = state ? m_Bits0 | bit : m_Bits0 & ~bit;
            else m_Bits1 = state ? m_Bits1 | bit : m_Bits1 & ~bit;
        }
        public void Press(Key key) => Set(key, true);
        public void Release(Key key) => Set(key, false);
        public bool Get(Key key)
        {
            int i = (int)key;
            if (i <= 0 || i >= 128) return false;
            return ((i < 64 ? m_Bits0 : m_Bits1) & (1UL << (i & 63))) != 0;
        }

        bool IStateWriter.CanWrite(InputDevice device) => device is Keyboard;
        void IStateWriter.Write(InputDevice device)
        {
            var kb = (Keyboard)device;
            for (Key k = Key.Space; k <= Key.OEM5; k++) kb[k].WriteRaw(Get(k) ? 1f : 0f);
        }
    }

    /// <summary>Unity 의 MouseButton</summary>
    public enum MouseButton { Left = 0, Right = 1, Middle = 2, Forward = 3, Back = 4 }

    /// <summary>Unity 의 MouseState — position · delta · scroll · 단추 비트</summary>
    public struct MouseState : IInputStateTypeInfo, IStateWriter
    {
        public static FourCC Format => new FourCC('M', 'O', 'U', 'S');
        public FourCC format => Format;
        public Vector2 position;
        public Vector2 delta;
        public Vector2 scroll;
        public ushort buttons;
        public ushort clickCount;

        public MouseState WithButton(MouseButton button, bool state = true)
        {
            ushort bit = (ushort)(1 << (int)button);
            buttons = state ? (ushort)(buttons | bit) : (ushort)(buttons & ~bit);
            return this;
        }
        public MouseState WithPosition(Vector2 p) { position = p; return this; }
        public MouseState WithDelta(Vector2 d) { delta = d; return this; }
        public MouseState WithScroll(Vector2 s) { scroll = s; return this; }

        bool IStateWriter.CanWrite(InputDevice device) => device is Mouse;
        void IStateWriter.Write(InputDevice device)
        {
            var m = (Mouse)device;
            m.position.WriteRaw(position);
            m.delta.WriteRaw(delta);
            m.scroll.WriteRaw(scroll);
            m.leftButton.WriteRaw((buttons & 1) != 0 ? 1 : 0);
            m.rightButton.WriteRaw((buttons & 2) != 0 ? 1 : 0);
            m.middleButton.WriteRaw((buttons & 4) != 0 ? 1 : 0);
            m.forwardButton.WriteRaw((buttons & 8) != 0 ? 1 : 0);
            m.backButton.WriteRaw((buttons & 16) != 0 ? 1 : 0);
            m.clickCount.WriteRaw(clickCount);
        }
    }

    /// <summary>Unity 의 GamepadState — 단추 비트 (GamepadButton 번호) · 스틱 · 트리거</summary>
    public struct GamepadState : IInputStateTypeInfo, IStateWriter
    {
        public static FourCC Format => new FourCC('G', 'P', 'A', 'D');
        public FourCC format => Format;
        public uint buttons;
        public Vector2 leftStick;
        public Vector2 rightStick;
        public float leftTrigger;
        public float rightTrigger;

        public GamepadState(params GamepadButton[] buttons)
        {
            this.buttons = 0;
            leftStick = rightStick = default;
            leftTrigger = rightTrigger = 0;
            if (buttons != null)
                foreach (var b in buttons) WithButton(b);
        }

        public GamepadState WithButton(GamepadButton button, bool value = true)
        {
            if (button == GamepadButton.LeftTrigger) { leftTrigger = value ? 1f : 0f; return this; }
            if (button == GamepadButton.RightTrigger) { rightTrigger = value ? 1f : 0f; return this; }
            uint bit = 1u << (int)button;
            buttons = value ? buttons | bit : buttons & ~bit;
            return this;
        }

        bool IStateWriter.CanWrite(InputDevice device) => device is Gamepad;
        void IStateWriter.Write(InputDevice device)
        {
            var g = (Gamepad)device;
            for (int b = 0; b <= 13; b++) g[(GamepadButton)b].WriteRaw((buttons & (1u << b)) != 0 ? 1f : 0f);
            g.leftStick.WriteRaw(leftStick);
            g.rightStick.WriteRaw(rightStick);
            g.leftTrigger.WriteRaw(leftTrigger);
            g.rightTrigger.WriteRaw(rightTrigger);
        }
    }

    /// <summary>Unity 의 TouchState — 손가락 하나</summary>
    public struct TouchState : IInputStateTypeInfo
    {
        public static FourCC Format => new FourCC('T', 'O', 'U', 'C');
        public FourCC format => Format;
        public int touchId;
        public Vector2 position;
        public Vector2 delta;
        public float pressure;
        public Vector2 radius;
        public TouchPhase phase;
        public byte tapCount;
        public bool isTap;
        public Vector2 startPosition;
        public double startTime;
        public bool isInProgress => phase == TouchPhase.Began || phase == TouchPhase.Moved || phase == TouchPhase.Stationary;
        public override string ToString() => $"{{ id={touchId} phase={phase} pos={position} delta={delta} }}";
    }

    /// <summary>Unity 의 InputState — 시각 · 컨트롤 값 직접 바꾸기 (시험)</summary>
    public static class InputState
    {
        /// <summary>입력 시각 (초, 엔진 시작부터 — Unity 의 InputState.currentTime)</summary>
        public static double currentTime => InputSystem.CurrentTime;

        /// <summary>컨트롤 값을 바로 바꾼다 (다음 InputSystem 갱신에 동작이 본다). 하드웨어 장치는 다음 프레임 하드웨어 값으로 돌아간다</summary>
        public static void Change<TValue>(InputControl<TValue> control, TValue value) where TValue : struct
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            InputSystem.QueueChange(control.device, () => control.WriteRaw(value));
        }

        public static void Change<TState>(InputDevice device, TState state) where TState : struct, IInputStateTypeInfo
            => InputSystem.QueueStateEvent(device, state);
    }
}

namespace NovaEngine.InputSystem
{
    /// <summary>Unity 의 InputSystem.TouchPhase (옛 NovaEngine.TouchPhase 와 번호가 다르다)</summary>
    public enum TouchPhase { None = 0, Began = 1, Moved = 2, Ended = 3, Canceled = 4, Stationary = 5 }
}
