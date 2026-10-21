using System;
using NovaEngine.InputSystem.Controls;

namespace NovaEngine.InputSystem
{
    /// <summary>
    /// Unity 의 IInputInteraction — 입력 모양 (누르고 있기 · 짧게 누르기 · 두 번 누르기 …) 을 동작 단계 (Started · Performed · Canceled) 로.
    /// 직접 만들기: class MyInteraction : IInputInteraction + InputSystem.RegisterInteraction&lt;MyInteraction&gt;()
    /// </summary>
    public interface IInputInteraction
    {
        void Process(ref InputInteractionContext context);
        void Reset();
    }

    public interface IInputInteraction<TValue> : IInputInteraction where TValue : struct { }

    /// <summary>Unity 의 InputInteractionContext — Process 안에서 단계를 바꾸고 시간 제한을 건다</summary>
    public struct InputInteractionContext
    {
        internal InputAction m_Action;
        internal BindingState m_Binding;
        internal int m_Index;
        internal bool m_TimerExpired;

        public InputAction action => m_Action;
        public InputControl control => m_Binding.control;
        public InputActionPhase phase => m_Binding.states[m_Index].phase;
        public double time => InputSystem.CurrentTime;
        public double startTime => m_Binding.states[m_Index].startTime;
        public bool timerHasExpired => m_TimerExpired;
        public bool isWaiting => phase == InputActionPhase.Waiting;
        public bool isStarted => phase == InputActionPhase.Started;

        public float ComputeMagnitude() => m_Binding.magnitude;

        /// <summary>얼마나 눌렸나가 문턱 이상인가 (0 = 조금이라도)</summary>
        public bool ControlIsActuated(float threshold = 0f) => threshold <= 0f ? m_Binding.magnitude > 0f : m_Binding.magnitude >= threshold;

        public void Started() => m_Action.InteractionChanged(m_Binding, m_Index, InputActionPhase.Started, InputActionPhase.Started);
        public void Performed() => m_Action.InteractionChanged(m_Binding, m_Index, InputActionPhase.Performed, InputActionPhase.Waiting);
        public void PerformedAndStayStarted() => m_Action.InteractionChanged(m_Binding, m_Index, InputActionPhase.Performed, InputActionPhase.Started);
        public void PerformedAndStayPerformed() => m_Action.InteractionChanged(m_Binding, m_Index, InputActionPhase.Performed, InputActionPhase.Performed);
        public void Canceled() => m_Action.InteractionChanged(m_Binding, m_Index, InputActionPhase.Canceled, InputActionPhase.Waiting);
        public void Waiting() => m_Action.InteractionChanged(m_Binding, m_Index, InputActionPhase.Waiting, InputActionPhase.Waiting);

        public void SetTimeout(float seconds)
        {
            ref var s = ref m_Binding.states[m_Index];
            s.timeout = InputSystem.CurrentTime + seconds;
            if (s.totalTimeout <= 0) s.totalTimeout = seconds;
        }

        public void SetTotalTimeoutCompletionTime(float seconds) => m_Binding.states[m_Index].totalTimeout = seconds;

        public TValue ReadValue<TValue>() where TValue : struct => ValueUtil.As<TValue>(m_Binding.value, m_Action, m_Binding.control);
    }
}

namespace NovaEngine.InputSystem.Interactions
{
    /// <summary>누르고 있기: 누르면 Started, duration 동안 누르고 있으면 Performed (놓을 때 Canceled), 일찍 놓으면 Canceled</summary>
    public class HoldInteraction : IInputInteraction
    {
        public float duration;
        public float pressPoint;
        float durationOrDefault => duration > 0f ? duration : InputSystem.settings.defaultHoldTime;
        float pressPointOrDefault => pressPoint > 0f ? pressPoint : InputSystem.settings.defaultButtonPressPoint;
        double m_TimePressed;

        public void Process(ref InputInteractionContext context)
        {
            if (context.timerHasExpired)
            {
                context.PerformedAndStayPerformed();
                return;
            }
            switch (context.phase)
            {
                case InputActionPhase.Waiting:
                    if (context.ControlIsActuated(pressPointOrDefault))
                    {
                        m_TimePressed = context.time;
                        context.Started();
                        context.SetTimeout(durationOrDefault);
                    }
                    break;
                case InputActionPhase.Started:
                    if (context.time - m_TimePressed >= durationOrDefault)
                        context.PerformedAndStayPerformed();
                    else if (!context.ControlIsActuated())
                        context.Canceled();
                    break;
                case InputActionPhase.Performed:
                    if (!context.ControlIsActuated(pressPointOrDefault))
                        context.Canceled();
                    break;
            }
        }

        public void Reset() => m_TimePressed = 0;
    }

    /// <summary>짧게 누르기: duration 안에 누르고 놓으면 Performed, 오래 누르면 Canceled</summary>
    public class TapInteraction : IInputInteraction
    {
        public float duration;
        public float pressPoint;
        float durationOrDefault => duration > 0f ? duration : InputSystem.settings.defaultTapTime;
        float pressPointOrDefault => pressPoint > 0f ? pressPoint : InputSystem.settings.defaultButtonPressPoint;
        float releasePointOrDefault => pressPointOrDefault * InputSystem.settings.buttonReleaseThreshold;
        double m_TapStartTime;

        public void Process(ref InputInteractionContext context)
        {
            if (context.timerHasExpired)
            {
                context.Canceled();
                return;
            }
            if (context.isWaiting && context.ControlIsActuated(pressPointOrDefault))
            {
                m_TapStartTime = context.time;
                context.Started();
                context.SetTimeout(durationOrDefault + 0.00001f);
                return;
            }
            if (context.isStarted && !context.ControlIsActuated(releasePointOrDefault))
            {
                if (context.time - m_TapStartTime <= durationOrDefault) context.Performed();
                else context.Canceled();
            }
        }

        public void Reset() => m_TapStartTime = 0;
    }

    /// <summary>천천히 누르기: duration 넘게 누른 뒤 놓으면 Performed, 일찍 놓으면 Canceled</summary>
    public class SlowTapInteraction : IInputInteraction
    {
        public float duration;
        public float pressPoint;
        float durationOrDefault => duration > 0f ? duration : InputSystem.settings.defaultSlowTapTime;
        float pressPointOrDefault => pressPoint > 0f ? pressPoint : InputSystem.settings.defaultButtonPressPoint;
        double m_SlowTapStartTime;

        public void Process(ref InputInteractionContext context)
        {
            if (context.isWaiting && context.ControlIsActuated(pressPointOrDefault))
            {
                m_SlowTapStartTime = context.time;
                context.Started();
                return;
            }
            if (context.isStarted && !context.ControlIsActuated(pressPointOrDefault))
            {
                if (context.time - m_SlowTapStartTime >= durationOrDefault) context.Performed();
                else context.Canceled();
            }
        }

        public void Reset() => m_SlowTapStartTime = 0;
    }

    /// <summary>여러 번 누르기: tapCount 번을 각각 tapTime 안에, 사이 간격 tapDelay 안에 누르면 Performed</summary>
    public class MultiTapInteraction : IInputInteraction<float>
    {
        public float tapTime;
        public float tapDelay;
        public int tapCount = 2;
        public float pressPoint;
        float tapTimeOrDefault => tapTime > 0f ? tapTime : InputSystem.settings.defaultTapTime;
        float tapDelayOrDefault => tapDelay > 0f ? tapDelay : InputSystem.settings.multiTapDelayTime;
        float pressPointOrDefault => pressPoint > 0f ? pressPoint : InputSystem.settings.defaultButtonPressPoint;
        float releasePointOrDefault => pressPointOrDefault * InputSystem.settings.buttonReleaseThreshold;

        enum TapPhase { None, WaitingForNextRelease, WaitingForNextPress }
        TapPhase m_CurrentTapPhase;
        int m_CurrentTapCount;
        double m_CurrentTapStartTime, m_LastTapReleaseTime;

        public void Process(ref InputInteractionContext context)
        {
            if (context.timerHasExpired)
            {
                context.Canceled();
                return;
            }
            switch (m_CurrentTapPhase)
            {
                case TapPhase.None:
                    if (context.ControlIsActuated(pressPointOrDefault))
                    {
                        m_CurrentTapPhase = TapPhase.WaitingForNextRelease;
                        m_CurrentTapStartTime = context.time;
                        context.Started();
                        context.SetTimeout(tapTimeOrDefault);
                        context.SetTotalTimeoutCompletionTime(tapTimeOrDefault * tapCount + (tapCount - 1) * tapDelayOrDefault);
                    }
                    break;
                case TapPhase.WaitingForNextRelease:
                    if (!context.ControlIsActuated(releasePointOrDefault))
                    {
                        if (context.time - m_CurrentTapStartTime <= tapTimeOrDefault)
                        {
                            ++m_CurrentTapCount;
                            if (m_CurrentTapCount >= tapCount)
                                context.Performed();
                            else
                            {
                                m_CurrentTapPhase = TapPhase.WaitingForNextPress;
                                m_LastTapReleaseTime = context.time;
                                context.SetTimeout(tapDelayOrDefault);
                            }
                        }
                        else context.Canceled();
                    }
                    break;
                case TapPhase.WaitingForNextPress:
                    if (context.ControlIsActuated(pressPointOrDefault))
                    {
                        if (context.time - m_LastTapReleaseTime <= tapDelayOrDefault)
                        {
                            m_CurrentTapPhase = TapPhase.WaitingForNextRelease;
                            m_CurrentTapStartTime = context.time;
                            context.SetTimeout(tapTimeOrDefault);
                        }
                        else context.Canceled();
                    }
                    break;
            }
        }

        public void Reset()
        {
            m_CurrentTapPhase = TapPhase.None;
            m_CurrentTapCount = 0;
            m_CurrentTapStartTime = 0;
            m_LastTapReleaseTime = 0;
        }
    }

    /// <summary>Unity 의 PressBehavior</summary>
    public enum PressBehavior { PressOnly = 0, ReleaseOnly = 1, PressAndRelease = 2 }

    /// <summary>누름: PressOnly = 누를 때 Performed, ReleaseOnly = 놓을 때, PressAndRelease = 누를 때와 놓을 때 모두</summary>
    public class PressInteraction : IInputInteraction
    {
        public float pressPoint;
        public PressBehavior behavior;
        float pressPointOrDefault => pressPoint > 0f ? pressPoint : InputSystem.settings.defaultButtonPressPoint;
        float releasePointOrDefault => pressPointOrDefault * InputSystem.settings.buttonReleaseThreshold;
        bool m_WaitingForRelease;

        public void Process(ref InputInteractionContext context)
        {
            float actuation = context.ComputeMagnitude();
            switch (behavior)
            {
                case PressBehavior.PressOnly:
                    if (m_WaitingForRelease)
                    {
                        if (actuation <= releasePointOrDefault)
                        {
                            m_WaitingForRelease = false;
                            if (actuation <= 0f) context.Canceled();
                            else context.Started();
                        }
                    }
                    else if (actuation >= pressPointOrDefault)
                    {
                        m_WaitingForRelease = true;
                        context.PerformedAndStayPerformed();
                    }
                    else if (actuation > 0f && !context.isStarted)
                        context.Started();
                    else if (actuation <= 0f && context.isStarted)
                        context.Canceled();
                    break;
                case PressBehavior.ReleaseOnly:
                    if (m_WaitingForRelease)
                    {
                        if (actuation <= releasePointOrDefault)
                        {
                            m_WaitingForRelease = false;
                            context.Performed();
                            context.Canceled();
                        }
                    }
                    else if (actuation >= pressPointOrDefault)
                    {
                        m_WaitingForRelease = true;
                        if (!context.isStarted) context.Started();
                    }
                    else
                    {
                        bool started = context.isStarted;
                        if (actuation > 0f && !started) context.Started();
                        else if (actuation <= 0f && started) context.Canceled();
                    }
                    break;
                case PressBehavior.PressAndRelease:
                    if (m_WaitingForRelease)
                    {
                        if (actuation <= releasePointOrDefault)
                        {
                            m_WaitingForRelease = false;
                            context.Performed();
                            if (actuation <= 0f) context.Canceled();
                        }
                    }
                    else if (actuation >= pressPointOrDefault)
                    {
                        m_WaitingForRelease = true;
                        context.PerformedAndStayPerformed();
                    }
                    else
                    {
                        bool started = context.isStarted;
                        if (actuation > 0f && !started) context.Started();
                        else if (actuation <= 0f && started) context.Canceled();
                    }
                    break;
            }
        }

        public void Reset() => m_WaitingForRelease = false;
    }
}

namespace NovaEngine.InputSystem
{
    // 바인딩에 interactions 가 없을 때 (Unity 의 기본 동작)
    //  Value: 움직이면 Started → Performed, 값이 바뀔 때마다 Performed, 0 으로 돌아가면 Canceled
    //  Button: 움직이면 Started, 누름 문턱을 넘으면 Performed, 놓기 문턱 아래로 (또는 Performed 전에 0) 오면 Canceled
    internal sealed class DefaultInteraction : IInputInteraction
    {
        internal InputActionType m_Type;
        internal DefaultInteraction(InputActionType type) { m_Type = type; }

        public void Process(ref InputInteractionContext context)
        {
            var b = context.m_Binding;
            if (m_Type == InputActionType.Value)
            {
                if (b.magnitude > 0f)
                {
                    if (context.isWaiting) { context.Started(); context.PerformedAndStayStarted(); }
                    else context.PerformedAndStayStarted();
                }
                else if (!context.isWaiting)
                    context.Canceled();
                return;
            }
            float press = ValueUtil.PressPoint(b.control);
            float release = press * InputSystem.settings.buttonReleaseThreshold;
            switch (context.phase)
            {
                case InputActionPhase.Waiting:
                    if (b.magnitude > 0f)
                    {
                        context.Started();
                        if (b.magnitude >= press) context.PerformedAndStayPerformed();
                    }
                    break;
                case InputActionPhase.Started:
                    if (b.magnitude >= press) context.PerformedAndStayPerformed();
                    else if (b.magnitude <= 0f) context.Canceled();
                    break;
                case InputActionPhase.Performed:
                    if (b.magnitude <= release) context.Canceled();
                    break;
            }
        }

        public void Reset() { }
    }
}
