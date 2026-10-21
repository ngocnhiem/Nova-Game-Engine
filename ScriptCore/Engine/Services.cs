using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NovaEngine.Interop;

namespace NovaEngine
{
    // ------------------------------------------------------------------ Time
    public static class Time
    {
        static TimeData s_Data;
        static int s_FetchedFrame = -1;
        internal static bool s_InFixedUpdate;

        static unsafe ref TimeData Data
        {
            get
            {
                // 한 프레임에 한 번만 네이티브에서 가져온다 (Bridge 가 프레임 시작마다 무효화)
                if (s_FetchedFrame != Bridge.FrameIndex)
                {
                    fixed (TimeData* p = &s_Data) Native.Api.Time_Get(p);
                    s_FetchedFrame = Bridge.FrameIndex;
                }
                return ref s_Data;
            }
        }

        public static float deltaTime => s_InFixedUpdate ? Data.fixedDeltaTime : Data.deltaTime;
        public static float unscaledDeltaTime => Data.unscaledDeltaTime;
        public static float smoothDeltaTime => deltaTime;
        public static float time => Data.time;
        public static float timeSinceLevelLoad => Data.time;
        public static float unscaledTime => Data.unscaledTime;
        public static float fixedDeltaTime { get => Data.fixedDeltaTime; set { } }
        public static float fixedTime => Data.time;
        public static float realtimeSinceStartup => Data.realtimeSinceStartup;
        public static int frameCount => Data.frameCount;
        public static float timeScale { get => Data.timeScale; set { } }
    }

    // ------------------------------------------------------------------ Debug
    public static class Debug
    {
        public static void Log(object message) => Write(0, message);
        public static void Log(object message, Object context) => Write(0, message);
        public static void LogWarning(object message) => Write(1, message);
        public static void LogWarning(object message, Object context) => Write(1, message);
        public static void LogError(object message) => Write(2, message);
        public static void LogError(object message, Object context) => Write(2, message);
        public static void LogFormat(string format, params object[] args) => Write(0, string.Format(format, args));
        public static void LogWarningFormat(string format, params object[] args) => Write(1, string.Format(format, args));
        public static void LogErrorFormat(string format, params object[] args) => Write(2, string.Format(format, args));
        public static void LogException(Exception e) => Bridge.LogException(e);
        public static void Assert(bool condition, object message = null) { if (!condition) Write(2, "Assertion failed" + (message != null ? ": " + message : "")); }
        public static void DrawLine(Vector3 start, Vector3 end, Color color = default, float duration = 0f) { }
        public static void DrawRay(Vector3 start, Vector3 dir, Color color = default, float duration = 0f) { }

        static void Write(int level, object message)
        {
            string text = message == null ? "Null" : message.ToString();
            // 호출한 사용자 코드의 위치 (Console 더블클릭 → 파일:줄)
            var st = new StackTrace(2, true);
            Bridge.Log(level, text, Bridge.FormatStack(st, out string file, out int line), file, line);
        }
    }

    // Unity ThreadPriority: Application.backgroundLoadingPriority — LoadSceneAsync 가 프레임마다 씬을 미리 짓는 시간 (2 · 4 · 10 · 50 ms)
    public enum ThreadPriority { Low = 0, BelowNormal = 1, Normal = 2, High = 4 }

    // ------------------------------------------------------------------ Application / Screen
    public static class Application
    {
        // 비동기 씬 읽기가 프레임마다 쓰는 시간 (기본 BelowNormal = 4 ms). 높을수록 빨리 끝나고 프레임이 길어진다 (Play 를 멈추면 기본으로)
        public static unsafe ThreadPriority backgroundLoadingPriority
        {
            get => (ThreadPriority)Native.Api.App_LoadingPriority(0, 0);
            set => Native.Api.App_LoadingPriority(1, (int)value);
        }
        public static bool isPlaying => true;
        // 빌드된 게임(플레이어)인지는 네이티브가 알려 준다
        public static unsafe bool isEditor => Native.Api.App_Info(0) == 0;
        public static bool isFocused => true;
        // 0 이하 = 제한 없음 (기본 -1). 엔진이 프레임 사이를 기다려 맞춘다 (재생 중에만, Play 를 멈추면 -1)
        public static unsafe int targetFrameRate
        {
            get => Native.Api.App_TargetFrameRate(0, 0);
            set => Native.Api.App_TargetFrameRate(1, value);
        }
        public static unsafe string productName => Native.Str(Native.Api.App_ProductName()) ?? "NOVA Game";
        // 저장 폴더 (Unity 와 같은 자리 — Windows: %USERPROFILE%\AppData\LocalLow\<회사>\<제품>, 안드로이드: 앱 파일 폴더)
        public static unsafe string persistentDataPath => Native.Str(Native.Api.App_Path(0)) ?? "";
        public static unsafe string dataPath => Native.Str(Native.Api.App_Path(1)) ?? "";
        public static unsafe string companyName => Native.Str(Native.Api.App_Path(2)) ?? "";
        public static unsafe string version => Native.Str(Native.Api.App_Path(3)) ?? "";
        public static unsafe string temporaryCachePath => Native.Str(Native.Api.App_Path(4)) ?? "";
        public static RuntimePlatform platform => (RuntimePlatform)Platform.NovaApp_Platform();
        public static bool isMobilePlatform => platform == RuntimePlatform.Android;
        // Unity 와 같이 에디터에서는 무시, 빌드된 게임은 종료
        public static unsafe void Quit()
        {
            if (isEditor) Debug.Log("Application.Quit() is ignored in the editor.");
            else Native.Api.App_Quit();
        }
    }
    // Unity 의 PlayerPrefs: 게임을 껐다 켜도 남는 작은 값 (설정 · 최고 점수 …). 종류가 다른 키는 없는 것으로 (GetInt 로 실수 키를 읽으면 기본값)
    //  저장 자리: 에디터 = 프로젝트의 Library/PlayerPrefs.json, Windows 게임 = persistentDataPath/PlayerPrefs.json, 안드로이드 = 앱 파일 폴더, 웹 = 브라우저 localStorage
    public static class PlayerPrefs
    {
        public static unsafe void SetInt(string key, int value) { fixed (byte* k = Native.Utf8(key)) Native.Api.Prefs_SetInt(k, value); }
        public static int GetInt(string key) => GetInt(key, 0);
        public static unsafe int GetInt(string key, int defaultValue) { fixed (byte* k = Native.Utf8(key)) return Native.Api.Prefs_GetInt(k, defaultValue); }
        public static unsafe void SetFloat(string key, float value) { fixed (byte* k = Native.Utf8(key)) Native.Api.Prefs_SetFloat(k, value); }
        public static float GetFloat(string key) => GetFloat(key, 0f);
        public static unsafe float GetFloat(string key, float defaultValue) { fixed (byte* k = Native.Utf8(key)) return Native.Api.Prefs_GetFloat(k, defaultValue); }
        public static unsafe void SetString(string key, string value)
        {
            fixed (byte* k = Native.Utf8(key)) fixed (byte* v = Native.Utf8(value ?? "")) Native.Api.Prefs_SetString(k, v);
        }
        public static string GetString(string key) => GetString(key, "");
        public static unsafe string GetString(string key, string defaultValue)
        {
            fixed (byte* k = Native.Utf8(key)) { byte* v = Native.Api.Prefs_GetString(k); return v == null ? defaultValue : Native.Str(v); }
        }
        public static unsafe bool HasKey(string key) { fixed (byte* k = Native.Utf8(key)) return Native.Api.Prefs_Has(k) != 0; }
        public static unsafe void DeleteKey(string key) { if (key == null) return; fixed (byte* k = Native.Utf8(key)) Native.Api.Prefs_Delete(k); }
        public static unsafe void DeleteAll() => Native.Api.Prefs_Delete(null);
        public static unsafe void Save() => Native.Api.Prefs_Save();
    }

    // Unity 와 같은 값
    public enum RuntimePlatform { WindowsPlayer = 2, WindowsEditor = 7, Android = 11, WebGLPlayer = 17 }
    public enum ScreenOrientation { Portrait = 1, PortraitUpsideDown = 2, LandscapeLeft = 3, LandscapeRight = 4, AutoRotation = 5 }

    // 엔진이 이름으로 내보낸 플랫폼 함수 (Source/Scripting/PlatformBindings.cpp — Windows 는 NovaCore.dll, 안드로이드는 libnova.so)
    internal static unsafe class Platform
    {
        const string Lib = "NovaCore";
        [DllImport(Lib)] internal static extern int NovaApp_Platform();
        [DllImport(Lib)] internal static extern int NovaInput_TouchCount();
        [DllImport(Lib)] internal static extern int NovaInput_GetTouch(int index, float* out7);
        [DllImport(Lib)] internal static extern void NovaScreen_SafeArea(float* out4);
        [DllImport(Lib)] internal static extern int NovaScreen_GetOrientation();
        [DllImport(Lib)] internal static extern void NovaScreen_SetOrientation(int orientation);
    }

    public static class Screen
    {
        public static unsafe int width { get { int w, h; Native.Api.Screen_Get(&w, &h); return w; } }
        public static unsafe int height { get { int w, h; Native.Api.Screen_Get(&w, &h); return h; } }
        // 노치 · 둥근 모서리에 가리지 않는 영역 (게임 화면 픽셀, 왼쪽 아래 기준). Windows 는 화면 전체
        public static unsafe Rect safeArea { get { float* r = stackalloc float[4]; Platform.NovaScreen_SafeArea(r); return new Rect(r[0], r[1], r[2], r[3]); } }
        // 지금 화면 방향 (가로 = LandscapeLeft, 세로 = Portrait). 넣으면 그 방향으로 돌린다 (안드로이드 — AutoRotation 은 기기 방향을 따른다)
        public static ScreenOrientation orientation
        {
            get => (ScreenOrientation)Platform.NovaScreen_GetOrientation();
            set => Platform.NovaScreen_SetOrientation((int)value);
        }
    }

    // ------------------------------------------------------------------ Input
    public enum KeyCode
    {
        None = 0, Backspace = 8, Tab = 9, Clear = 12, Return = 13, Pause = 19, Escape = 27, Space = 32,
        Quote = 39, Comma = 44, Minus = 45, Period = 46, Slash = 47,
        Alpha0 = 48, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
        Semicolon = 59, Equals = 61, LeftBracket = 91, Backslash = 92, RightBracket = 93, BackQuote = 96,
        A = 97, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Delete = 127,
        Keypad0 = 256, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
        KeypadPeriod = 266, KeypadDivide, KeypadMultiply, KeypadMinus, KeypadPlus, KeypadEnter,
        UpArrow = 273, DownArrow, RightArrow, LeftArrow, Insert, Home, End, PageUp, PageDown,
        F1 = 282, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        CapsLock = 301, RightShift = 303, LeftShift, RightControl, LeftControl, RightAlt, LeftAlt,
        Mouse0 = 323, Mouse1, Mouse2, Mouse3, Mouse4, Mouse5, Mouse6,
        // 게임패드 단추 (Unity 와 같은 번호 — Xbox 패드: 0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 6 Back, 7 Start, 8 왼 스틱, 9 오른 스틱)
        JoystickButton0 = 330, JoystickButton1, JoystickButton2, JoystickButton3, JoystickButton4, JoystickButton5, JoystickButton6, JoystickButton7, JoystickButton8, JoystickButton9,
        JoystickButton10, JoystickButton11, JoystickButton12, JoystickButton13, JoystickButton14, JoystickButton15, JoystickButton16, JoystickButton17, JoystickButton18, JoystickButton19,
        Joystick1Button0 = 350, Joystick1Button1, Joystick1Button2, Joystick1Button3, Joystick1Button4, Joystick1Button5, Joystick1Button6, Joystick1Button7, Joystick1Button8, Joystick1Button9,
        Joystick1Button10, Joystick1Button11, Joystick1Button12, Joystick1Button13, Joystick1Button14, Joystick1Button15, Joystick1Button16, Joystick1Button17, Joystick1Button18, Joystick1Button19,
        Joystick2Button0 = 370, Joystick2Button1, Joystick2Button2, Joystick2Button3, Joystick2Button4, Joystick2Button5, Joystick2Button6, Joystick2Button7, Joystick2Button8, Joystick2Button9,
        Joystick2Button10, Joystick2Button11, Joystick2Button12, Joystick2Button13, Joystick2Button14, Joystick2Button15, Joystick2Button16, Joystick2Button17, Joystick2Button18, Joystick2Button19,
        Joystick3Button0 = 390, Joystick3Button1, Joystick3Button2, Joystick3Button3, Joystick3Button4, Joystick3Button5, Joystick3Button6, Joystick3Button7, Joystick3Button8, Joystick3Button9,
        Joystick3Button10, Joystick3Button11, Joystick3Button12, Joystick3Button13, Joystick3Button14, Joystick3Button15, Joystick3Button16, Joystick3Button17, Joystick3Button18, Joystick3Button19,
        Joystick4Button0 = 410, Joystick4Button1, Joystick4Button2, Joystick4Button3, Joystick4Button4, Joystick4Button5, Joystick4Button6, Joystick4Button7, Joystick4Button8, Joystick4Button9,
        Joystick4Button10, Joystick4Button11, Joystick4Button12, Joystick4Button13, Joystick4Button14, Joystick4Button15, Joystick4Button16, Joystick4Button17, Joystick4Button18, Joystick4Button19,
    }

    /// <summary>Unity 의 CursorLockMode</summary>
    public enum CursorLockMode { None = 0, Locked = 1, Confined = 2 }

    /// <summary>
    /// Unity 의 Cursor — Locked = 커서를 숨기고 게임 화면 가운데에 묶는다 (마우스 이동량은 Mouse.delta · Input.GetAxis("Mouse X")),
    /// Confined = 게임 창 안에만. 에디터: Game 뷰에 입력 포커스가 있을 때만, Esc 로 풀린다 (Unity 와 같이)
    /// </summary>
    public static class Cursor
    {
        public static CursorLockMode lockState
        {
            get => CursorState.Current();
            set { CursorState.s_Lock = value; CursorState.Apply(); }
        }

        public static bool visible
        {
            get => CursorState.s_Visible;
            set { CursorState.s_Visible = value; CursorState.Apply(); }
        }
    }

    internal static class CursorState
    {
        internal static CursorLockMode s_Lock = CursorLockMode.None;
        internal static bool s_Visible = true;

        // 에디터에서 Esc 로 풀면 None (Unity 와 같이)
        internal static CursorLockMode Current()
        {
            try { return (CursorLockMode)NovaEngine.InputSystem.InputNative.NovaInputSys_GetCursorLock(); }
            catch (EntryPointNotFoundException) { return s_Lock; }
            catch (DllNotFoundException) { return s_Lock; }
        }

        internal static void Apply()
        {
            try { NovaEngine.InputSystem.InputNative.NovaInputSys_SetCursor((int)s_Lock, s_Visible ? 1 : 0); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        internal static void Reset()
        {
            s_Lock = CursorLockMode.None;
            s_Visible = true;
            Apply();
        }
    }

    // 옛 Input 의 게임패드 (Unity 의 Input Manager 기본 축 · joystick button) — 프레임에 한 번 엔진 상태를 읽는다
    internal static unsafe class LegacyPads
    {
        static int s_Frame = -1;
        static readonly uint[] s_Buttons = new uint[8], s_Prev = new uint[8];
        static readonly float[] s_Axes = new float[48];
        static readonly bool[] s_Connected = new bool[8];
        static Vector2 s_MouseDelta;
        static bool s_HasDelta;

        internal static bool HasMouseDelta { get { Refresh(); return s_HasDelta; } }
        internal static Vector2 MouseDelta { get { Refresh(); return s_MouseDelta; } }

        static void Refresh()
        {
            if (s_Frame == Time.frameCount) return;
            s_Frame = Time.frameCount;
            Array.Copy(s_Buttons, s_Prev, 8);
            NovaEngine.InputSystem.NativeInputState st = default;
            int ok = 0;
            try { ok = NovaEngine.InputSystem.InputNative.NovaInputSys_GetState(&st, sizeof(NovaEngine.InputSystem.NativeInputState)); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            s_HasDelta = ok != 0;
            s_MouseDelta = ok != 0 ? new Vector2(st.mouseDX, st.mouseDY) : Vector2.zero;
            for (int i = 0; i < 8; i++)
            {
                s_Connected[i] = ok != 0 && st.padConnected[i] != 0;
                s_Buttons[i] = s_Connected[i] ? Map(st.padButtons[i]) : 0;
                for (int k = 0; k < 6; k++) s_Axes[i * 6 + k] = s_Connected[i] ? st.padAxes[i * 6 + k] : 0f;
            }
        }

        // XInput 비트 → Unity joystick button 번호
        static uint Map(uint x)
        {
            uint b = 0;
            if ((x & 0x1000) != 0) b |= 1u << 0;   // A
            if ((x & 0x2000) != 0) b |= 1u << 1;   // B
            if ((x & 0x4000) != 0) b |= 1u << 2;   // X
            if ((x & 0x8000) != 0) b |= 1u << 3;   // Y
            if ((x & 0x0100) != 0) b |= 1u << 4;   // LB
            if ((x & 0x0200) != 0) b |= 1u << 5;   // RB
            if ((x & 0x0020) != 0) b |= 1u << 6;   // Back
            if ((x & 0x0010) != 0) b |= 1u << 7;   // Start
            if ((x & 0x0040) != 0) b |= 1u << 8;   // 왼 스틱 누름
            if ((x & 0x0080) != 0) b |= 1u << 9;   // 오른 스틱 누름
            return b;
        }

        // joystick = 0 이면 모든 패드, 1 ~ 4 는 그 패드
        internal static bool Button(int joystick, int button, int mode)
        {
            Refresh();
            for (int i = 0; i < 8; i++)
            {
                if (joystick > 0 && Slot(joystick) != i) continue;
                bool now = (s_Buttons[i] & (1u << button)) != 0, before = (s_Prev[i] & (1u << button)) != 0;
                if (mode == 0 ? now : mode == 1 ? now && !before : !now && before) return true;
            }
            return false;
        }

        // n 번째로 연결된 패드의 칸
        static int Slot(int joystick)
        {
            int n = 0;
            for (int i = 0; i < 8; i++)
                if (s_Connected[i] && ++n == joystick) return i;
            return -1;
        }

        // 축: 0 = 왼 스틱 X, 1 = 왼 스틱 Y (위 +), 3 = 오른 스틱 X, 4 = 오른 스틱 Y, 2 = 트리거 (오른 - 왼). Unity 기본 dead 0.19
        internal static float Axis(int axis)
        {
            Refresh();
            float best = 0f;
            for (int i = 0; i < 8; i++)
            {
                if (!s_Connected[i]) continue;
                float v = axis switch
                {
                    0 => s_Axes[i * 6 + 0],
                    1 => s_Axes[i * 6 + 1],
                    3 => s_Axes[i * 6 + 2],
                    4 => s_Axes[i * 6 + 3],
                    _ => s_Axes[i * 6 + 5] - s_Axes[i * 6 + 4],
                };
                if (Math.Abs(v) < 0.19f) v = 0f;
                if (Math.Abs(v) > Math.Abs(best)) best = v;
            }
            return best;
        }

        internal static string[] Names()
        {
            Refresh();
            var l = new List<string>();
            for (int i = 0; i < 8; i++) if (s_Connected[i]) l.Add(i < 4 ? "Controller (Xbox One For Windows)" : "NOVA Injected Gamepad");
            return l.ToArray();
        }
    }

    public enum TouchPhase { Began, Moved, Stationary, Ended, Canceled }
    public struct Touch
    {
        public int fingerId;
        public Vector2 position, deltaPosition;
        public float deltaTime;
        public int tapCount;
        public TouchPhase phase;
        public Vector2 rawPosition => position;
        public float pressure => 1f;
        public float radius => 0f;
    }

    public static class Input
    {
        // KeyCode → Windows 가상 키 (마우스는 음수 = 버튼 번호)
        static int ToVk(KeyCode k)
        {
            int c = (int)k;
            if (k >= KeyCode.A && k <= KeyCode.Z) return 'A' + (c - (int)KeyCode.A);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return '0' + (c - (int)KeyCode.Alpha0);
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return 0x60 + (c - (int)KeyCode.Keypad0);
            if (k >= KeyCode.F1 && k <= KeyCode.F12) return 0x70 + (c - (int)KeyCode.F1);
            if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6) return -1 - (c - (int)KeyCode.Mouse0);
            switch (k)
            {
                case KeyCode.Backspace: return 0x08; case KeyCode.Tab: return 0x09; case KeyCode.Return: return 0x0D;
                case KeyCode.KeypadEnter: return 0x0D; case KeyCode.Pause: return 0x13; case KeyCode.Escape: return 0x1B;
                case KeyCode.Space: return 0x20; case KeyCode.Delete: return 0x2E; case KeyCode.Insert: return 0x2D;
                case KeyCode.Home: return 0x24; case KeyCode.End: return 0x23; case KeyCode.PageUp: return 0x21; case KeyCode.PageDown: return 0x22;
                case KeyCode.UpArrow: return 0x26; case KeyCode.DownArrow: return 0x28; case KeyCode.LeftArrow: return 0x25; case KeyCode.RightArrow: return 0x27;
                case KeyCode.LeftShift: return 0xA0; case KeyCode.RightShift: return 0xA1; case KeyCode.LeftControl: return 0xA2;
                case KeyCode.RightControl: return 0xA3; case KeyCode.LeftAlt: return 0xA4; case KeyCode.RightAlt: return 0xA5;
                case KeyCode.CapsLock: return 0x14; case KeyCode.Minus: return 0xBD; case KeyCode.Equals: return 0xBB;
                case KeyCode.Comma: return 0xBC; case KeyCode.Period: return 0xBE; case KeyCode.Slash: return 0xBF;
                case KeyCode.Semicolon: return 0xBA; case KeyCode.Quote: return 0xDE; case KeyCode.LeftBracket: return 0xDB;
                case KeyCode.RightBracket: return 0xDD; case KeyCode.Backslash: return 0xDC; case KeyCode.BackQuote: return 0xC0;
                case KeyCode.KeypadPeriod: return 0x6E; case KeyCode.KeypadDivide: return 0x6F; case KeyCode.KeypadMultiply: return 0x6A;
                case KeyCode.KeypadMinus: return 0x6D; case KeyCode.KeypadPlus: return 0x6B;
            }
            return 0;
        }

        static unsafe bool Key(KeyCode k, int mode)
        {
            int kc = (int)k;
            if (kc >= (int)KeyCode.JoystickButton0 && kc <= (int)KeyCode.Joystick4Button19)
            {
                int joystick = (kc - (int)KeyCode.JoystickButton0) / 20, button = (kc - (int)KeyCode.JoystickButton0) % 20;
                return LegacyPads.Button(joystick, button, mode);
            }
            int vk = ToVk(k);
            if (vk < 0) return Native.Api.Input_GetMouseButton(-1 - vk, mode) != 0;
            return vk != 0 && Native.Api.Input_GetKey(vk, mode) != 0;
        }

        public static bool GetKey(KeyCode key) => Key(key, 0);
        public static bool GetKeyDown(KeyCode key) => Key(key, 1);
        public static bool GetKeyUp(KeyCode key) => Key(key, 2);
        public static bool GetKey(string name) => Key(Parse(name), 0);
        public static bool GetKeyDown(string name) => Key(Parse(name), 1);
        public static bool GetKeyUp(string name) => Key(Parse(name), 2);

        static KeyCode Parse(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (name.Length == 1 && name[0] >= 'a' && name[0] <= 'z') return KeyCode.A + (name[0] - 'a');
            if (name.Length == 1 && name[0] >= '0' && name[0] <= '9') return KeyCode.Alpha0 + (name[0] - '0');
            // "joystick button 0" · "joystick 1 button 3" (Unity 의 이름)
            if (name.StartsWith("joystick "))
            {
                string[] w = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 3 && w[1] == "button" && int.TryParse(w[2], out int jb) && jb >= 0 && jb < 20) return KeyCode.JoystickButton0 + jb;
                if (w.Length == 4 && w[2] == "button" && int.TryParse(w[1], out int jn) && int.TryParse(w[3], out int jb2) && jn >= 1 && jn <= 4 && jb2 >= 0 && jb2 < 20)
                    return KeyCode.JoystickButton0 + jn * 20 + jb2;
            }
            switch (name)
            {
                case "space": return KeyCode.Space; case "return": case "enter": return KeyCode.Return; case "escape": return KeyCode.Escape;
                case "tab": return KeyCode.Tab; case "backspace": return KeyCode.Backspace; case "up": return KeyCode.UpArrow;
                case "down": return KeyCode.DownArrow; case "left": return KeyCode.LeftArrow; case "right": return KeyCode.RightArrow;
                case "left shift": return KeyCode.LeftShift; case "right shift": return KeyCode.RightShift;
                case "left ctrl": return KeyCode.LeftControl; case "right ctrl": return KeyCode.RightControl;
                case "left alt": return KeyCode.LeftAlt; case "right alt": return KeyCode.RightAlt;
            }
            if (Enum.TryParse(name, true, out KeyCode k)) return k;
            return KeyCode.None;
        }

        // ---- 터치 (Unity 의 Input.touchCount · GetTouch · touches). 안드로이드 손가락, PC 는 Unity 처럼 0 개
        public static bool touchSupported => Application.platform == RuntimePlatform.Android;
        public static bool multiTouchEnabled { get => true; set { } }
        public static int touchCount => Platform.NovaInput_TouchCount();
        public static unsafe Touch GetTouch(int index)
        {
            float* t = stackalloc float[7];
            if (Platform.NovaInput_GetTouch(index, t) == 0) throw new ArgumentException("Index out of bounds.");
            return new Touch { fingerId = (int)t[0], position = new Vector2(t[1], t[2]), deltaPosition = new Vector2(t[3], t[4]), phase = (TouchPhase)(int)t[5], tapCount = (int)t[6], deltaTime = Time.deltaTime };
        }
        public static Touch[] touches
        {
            get
            {
                var all = new Touch[touchCount];
                for (int i = 0; i < all.Length; i++) all[i] = GetTouch(i);
                return all;
            }
        }

        public static unsafe bool GetMouseButton(int button) => Native.Api.Input_GetMouseButton(button, 0) != 0;
        public static unsafe bool GetMouseButtonDown(int button) => Native.Api.Input_GetMouseButton(button, 1) != 0;
        public static unsafe bool GetMouseButtonUp(int button) => Native.Api.Input_GetMouseButton(button, 2) != 0;
        public static unsafe Vector3 mousePosition { get { Vector4 m; Native.Api.Input_GetMouse(&m); return new Vector3(m.x, m.y, 0); } }
        public static unsafe Vector2 mouseScrollDelta { get { Vector4 m; Native.Api.Input_GetMouse(&m); return new Vector2(m.z, m.w); } }
        public static bool anyKey { get { for (int vk = 8; vk < 256; vk++) if (Native_Key(vk, 0)) return true; return GetMouseButton(0) || GetMouseButton(1); } }
        public static bool anyKeyDown { get { for (int vk = 8; vk < 256; vk++) if (Native_Key(vk, 1)) return true; return GetMouseButtonDown(0) || GetMouseButtonDown(1); } }
        static unsafe bool Native_Key(int vk, int mode) => Native.Api.Input_GetKey(vk, mode) != 0;

        // ---- 가상 축 (Unity 기본 Input Manager 와 같은 이름·값)
        class Axis { public float value; public int frame = -1; }
        static readonly Dictionary<string, Axis> s_Axes = new Dictionary<string, Axis>();
        static Vector3 s_LastMouse; static int s_MouseFrame = -1; static Vector2 s_MouseDelta;

        static float Raw(string axisName)
        {
            switch (axisName)
            {
                case "Horizontal": return Larger((GetKey(KeyCode.D) || GetKey(KeyCode.RightArrow) ? 1f : 0f) - (GetKey(KeyCode.A) || GetKey(KeyCode.LeftArrow) ? 1f : 0f), LegacyPads.Axis(0));
                case "Vertical": return Larger((GetKey(KeyCode.W) || GetKey(KeyCode.UpArrow) ? 1f : 0f) - (GetKey(KeyCode.S) || GetKey(KeyCode.DownArrow) ? 1f : 0f), LegacyPads.Axis(1));
                case "Mouse X": UpdateMouse(); return s_MouseDelta.x * 0.1f;
                case "Mouse Y": UpdateMouse(); return s_MouseDelta.y * 0.1f;
                case "Mouse ScrollWheel": return mouseScrollDelta.y * 0.1f;
                case "Jump": return GetKey(KeyCode.Space) || GetKey(KeyCode.JoystickButton3) ? 1f : 0f;
                case "Fire1": return GetKey(KeyCode.LeftControl) || GetMouseButton(0) || GetKey(KeyCode.JoystickButton0) ? 1f : 0f;
                case "Fire2": return GetKey(KeyCode.LeftAlt) || GetMouseButton(1) || GetKey(KeyCode.JoystickButton1) ? 1f : 0f;
                case "Fire3": return GetKey(KeyCode.LeftShift) || GetMouseButton(2) || GetKey(KeyCode.JoystickButton2) ? 1f : 0f;
                case "Submit": return GetKey(KeyCode.Return) || GetKey(KeyCode.Space) || GetKey(KeyCode.JoystickButton0) ? 1f : 0f;
                case "Cancel": return GetKey(KeyCode.Escape) || GetKey(KeyCode.JoystickButton1) ? 1f : 0f;
            }
            throw new ArgumentException($"Input Axis {axisName} is not setup.");
        }

        static void UpdateMouse()
        {
            if (s_MouseFrame == Time.frameCount) return;
            Vector3 m = mousePosition;
            // 엔진이 잰 이동량 (Cursor.lockState = Locked 로 커서가 가운데로 돌아가도)
            if (LegacyPads.HasMouseDelta) s_MouseDelta = s_MouseFrame < 0 ? Vector2.zero : LegacyPads.MouseDelta;
            else s_MouseDelta = s_MouseFrame < 0 ? Vector2.zero : new Vector2(m.x - s_LastMouse.x, m.y - s_LastMouse.y);
            s_LastMouse = m;
            s_MouseFrame = Time.frameCount;
        }

        public static float GetAxisRaw(string axisName) => Raw(axisName);

        static float Larger(float a, float b) => Math.Abs(b) > Math.Abs(a) ? b : a;

        /// <summary>연결된 게임패드 이름 (Unity 의 Input.GetJoystickNames)</summary>
        public static string[] GetJoystickNames() => LegacyPads.Names();

        // Unity 기본값: gravity 3, sensitivity 3, snap (방향이 바뀌면 0 부터)
        public static float GetAxis(string axisName)
        {
            if (axisName.StartsWith("Mouse")) return Raw(axisName);
            if (!s_Axes.TryGetValue(axisName, out var a)) s_Axes[axisName] = a = new Axis();
            if (a.frame != Time.frameCount)
            {
                float target = Raw(axisName), dt = Time.unscaledDeltaTime;
                if (target != 0 && Math.Sign(target) != Math.Sign(a.value) && a.value != 0) a.value = 0;
                a.value = target != 0 ? Mathf.MoveTowards(a.value, target, 3f * dt) : Mathf.MoveTowards(a.value, 0, 3f * dt);
                a.frame = Time.frameCount;
            }
            return a.value;
        }

        public static bool GetButton(string buttonName) => Raw(buttonName) != 0;
        public static bool GetButtonDown(string buttonName) => ButtonEdge(buttonName, 1);
        public static bool GetButtonUp(string buttonName) => ButtonEdge(buttonName, 2);
        static bool ButtonEdge(string b, int mode)
        {
            switch (b)
            {
                case "Jump": return Key(KeyCode.Space, mode) || Key(KeyCode.JoystickButton3, mode);
                case "Fire1": return Key(KeyCode.LeftControl, mode) || Key(KeyCode.Mouse0, mode) || Key(KeyCode.JoystickButton0, mode);
                case "Fire2": return Key(KeyCode.LeftAlt, mode) || Key(KeyCode.Mouse1, mode) || Key(KeyCode.JoystickButton1, mode);
                case "Fire3": return Key(KeyCode.LeftShift, mode) || Key(KeyCode.Mouse2, mode) || Key(KeyCode.JoystickButton2, mode);
                case "Submit": return Key(KeyCode.Return, mode) || Key(KeyCode.Space, mode) || Key(KeyCode.JoystickButton0, mode);
                case "Cancel": return Key(KeyCode.Escape, mode) || Key(KeyCode.JoystickButton1, mode);
            }
            throw new ArgumentException($"Input Button {b} is not setup.");
        }
    }
}
