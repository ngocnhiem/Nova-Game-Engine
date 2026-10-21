using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NovaEngine.InputSystem.Utilities
{
    /// <summary>Unity 의 ReadOnlyArray&lt;T&gt; — 배열 일부를 복사 없이 읽기 전용으로 (devices · bindings · controls …)</summary>
    public readonly struct ReadOnlyArray<TValue> : IReadOnlyList<TValue>
    {
        internal readonly TValue[] m_Array;
        internal readonly int m_StartIndex;
        internal readonly int m_Length;

        public ReadOnlyArray(TValue[] array)
        {
            m_Array = array;
            m_StartIndex = 0;
            m_Length = array?.Length ?? 0;
        }

        public ReadOnlyArray(TValue[] array, int index, int length)
        {
            m_Array = array;
            m_StartIndex = index;
            m_Length = length;
        }

        public int Count => m_Length;

        public TValue this[int index]
        {
            get
            {
                if ((uint)index >= (uint)m_Length) throw new ArgumentOutOfRangeException(nameof(index));
                return m_Array[m_StartIndex + index];
            }
        }

        public TValue[] ToArray()
        {
            var r = new TValue[m_Length];
            if (m_Length > 0) Array.Copy(m_Array, m_StartIndex, r, 0, m_Length);
            return r;
        }

        public int IndexOf(Predicate<TValue> predicate)
        {
            for (int i = 0; i < m_Length; i++)
                if (predicate(m_Array[m_StartIndex + i])) return i;
            return -1;
        }

        public Enumerator GetEnumerator() => new Enumerator(m_Array, m_StartIndex, m_Length);
        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static implicit operator ReadOnlyArray<TValue>(TValue[] array) => new ReadOnlyArray<TValue>(array);

        public override string ToString()
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < m_Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(m_Array[m_StartIndex + i]);
            }
            return sb.Append(']').ToString();
        }

        public struct Enumerator : IEnumerator<TValue>
        {
            readonly TValue[] m_Array;
            readonly int m_Start, m_End;
            int m_Index;
            internal Enumerator(TValue[] array, int start, int length) { m_Array = array; m_Start = start; m_End = start + length; m_Index = start - 1; }
            public bool MoveNext() => ++m_Index < m_End;
            public void Reset() => m_Index = m_Start - 1;
            public TValue Current => m_Array[m_Index];
            object IEnumerator.Current => Current;
            public void Dispose() { }
        }
    }

    public static class ReadOnlyArrayExtensions
    {
        public static bool Contains<TValue>(this ReadOnlyArray<TValue> array, TValue value) where TValue : IEquatable<TValue>
        {
            for (int i = 0; i < array.m_Length; i++)
                if (array.m_Array[array.m_StartIndex + i].Equals(value)) return true;
            return false;
        }

        public static bool ContainsReference<TValue>(this ReadOnlyArray<TValue> array, TValue value) where TValue : class
            => IndexOfReference(array, value) >= 0;

        public static int IndexOfReference<TValue>(this ReadOnlyArray<TValue> array, TValue value) where TValue : class
        {
            for (int i = 0; i < array.m_Length; i++)
                if (ReferenceEquals(array.m_Array[array.m_StartIndex + i], value)) return i;
            return -1;
        }
    }

    /// <summary>Unity 의 InputSystem.Utilities.Observable — onAnyButtonPress.CallOnce(...)</summary>
    public static class Observable
    {
        public static IObservable<TValue> Where<TValue>(this IObservable<TValue> source, Func<TValue, bool> predicate)
            => new WhereObservable<TValue>(source, predicate);

        public static IDisposable Call<TValue>(this IObservable<TValue> source, Action<TValue> action)
            => source.Subscribe(new ActionObserver<TValue>(action, false));

        public static IDisposable CallOnce<TValue>(this IObservable<TValue> source, Action<TValue> action)
        {
            var o = new ActionObserver<TValue>(action, true);
            o.Subscription = source.Subscribe(o);
            return o.Subscription;
        }

        sealed class ActionObserver<TValue> : IObserver<TValue>
        {
            readonly Action<TValue> m_Action;
            readonly bool m_Once;
            internal IDisposable Subscription;
            bool m_Done;
            public ActionObserver(Action<TValue> action, bool once) { m_Action = action; m_Once = once; }
            public void OnNext(TValue value)
            {
                if (m_Done) return;
                if (m_Once) { m_Done = true; Subscription?.Dispose(); }
                m_Action(value);
            }
            public void OnCompleted() { }
            public void OnError(Exception error) => Debug.LogException(error);
        }

        sealed class WhereObservable<TValue> : IObservable<TValue>
        {
            readonly IObservable<TValue> m_Source;
            readonly Func<TValue, bool> m_Predicate;
            public WhereObservable(IObservable<TValue> source, Func<TValue, bool> predicate) { m_Source = source; m_Predicate = predicate; }
            public IDisposable Subscribe(IObserver<TValue> observer) => m_Source.Subscribe(new Filter(observer, m_Predicate));
            sealed class Filter : IObserver<TValue>
            {
                readonly IObserver<TValue> m_Next;
                readonly Func<TValue, bool> m_Predicate;
                public Filter(IObserver<TValue> next, Func<TValue, bool> predicate) { m_Next = next; m_Predicate = predicate; }
                public void OnNext(TValue value) { if (m_Predicate(value)) m_Next.OnNext(value); }
                public void OnCompleted() => m_Next.OnCompleted();
                public void OnError(Exception error) => m_Next.OnError(error);
            }
        }
    }

    // 구독 목록 (onAnyButtonPress · onEvent)
    internal sealed class ObserverList<TValue> : IObservable<TValue>
    {
        readonly List<IObserver<TValue>> m_List = new List<IObserver<TValue>>();
        public int Count => m_List.Count;
        public IDisposable Subscribe(IObserver<TValue> observer)
        {
            m_List.Add(observer);
            return new Unsubscriber(this, observer);
        }
        public void Notify(TValue value)
        {
            if (m_List.Count == 0) return;
            foreach (var o in m_List.ToArray())
            {
                try { o.OnNext(value); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
        sealed class Unsubscriber : IDisposable
        {
            ObserverList<TValue> m_Owner;
            readonly IObserver<TValue> m_Observer;
            public Unsubscriber(ObserverList<TValue> owner, IObserver<TValue> observer) { m_Owner = owner; m_Observer = observer; }
            public void Dispose() { m_Owner?.m_List.Remove(m_Observer); m_Owner = null; }
        }
    }

    /// <summary>"name(a=1,b=true)" 하나 (Unity 의 NameAndParameters — interactions · processors · 합성 경로)</summary>
    public struct NameAndParameters
    {
        public string name;
        public ReadOnlyArray<NamedValue> parameters;

        public override string ToString()
        {
            if (parameters.Count == 0) return name;
            var sb = new StringBuilder(name).Append('(');
            for (int i = 0; i < parameters.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(parameters[i].name).Append('=').Append(parameters[i].value);
            }
            return sb.Append(')').ToString();
        }

        /// <summary>"Hold(duration=0.5),Tap" → 둘</summary>
        public static List<NameAndParameters> ParseMultiple(string text)
        {
            var list = new List<NameAndParameters>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',' || text[i] == ';')) i++;
                if (i >= text.Length) break;
                int start = i;
                while (i < text.Length && text[i] != '(' && text[i] != ',' && text[i] != ';') i++;
                var n = new NameAndParameters { name = text.Substring(start, i - start).Trim() };
                var ps = new List<NamedValue>();
                if (i < text.Length && text[i] == '(')
                {
                    int close = text.IndexOf(')', i);
                    if (close < 0) close = text.Length;
                    foreach (string part in text.Substring(i + 1, close - i - 1).Split(','))
                    {
                        string p = part.Trim();
                        if (p.Length == 0) continue;
                        int eq = p.IndexOf('=');
                        ps.Add(eq < 0 ? new NamedValue { name = p, value = "true" } : new NamedValue { name = p.Substring(0, eq).Trim(), value = p.Substring(eq + 1).Trim() });
                    }
                    i = close + 1;
                }
                n.parameters = ps.ToArray();
                if (n.name.Length > 0) list.Add(n);
            }
            return list;
        }

        public static NameAndParameters Parse(string text)
        {
            var l = ParseMultiple(text);
            return l.Count > 0 ? l[0] : default;
        }
    }

    public struct NamedValue
    {
        public string name;
        public string value;
        public override string ToString() => name + "=" + value;
    }

    internal static class ParamUtil
    {
        static readonly Dictionary<(Type, string), System.Reflection.FieldInfo> s_Fields = new Dictionary<(Type, string), System.Reflection.FieldInfo>();

        // 인자 이름 = 공개 필드 이름 (대소문자 무시) — Unity 의 interaction · processor · composite 매개변수와 같다
        internal static void Apply(object target, ReadOnlyArray<NamedValue> parameters)
        {
            if (target == null) return;
            Type t = target.GetType();
            foreach (NamedValue p in parameters)
            {
                if (!s_Fields.TryGetValue((t, p.name.ToLowerInvariant()), out var f))
                {
                    f = null;
                    foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        if (string.Equals(fi.Name, p.name, StringComparison.OrdinalIgnoreCase)) { f = fi; break; }
                    s_Fields[(t, p.name.ToLowerInvariant())] = f;
                }
                if (f == null) continue;
                try { f.SetValue(target, Convert(p.value, f.FieldType)); }
                catch { /* 형식이 맞지 않는 값은 기본값 (Unity 도 무시) */ }
            }
        }

        internal static object Convert(string value, Type type)
        {
            value = (value ?? string.Empty).Trim();
            if (type == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return (int)double.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (type.IsEnum)
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return Enum.ToObject(type, n);
                return Enum.Parse(type, value, true);
            }
            if (type == typeof(string)) return value;
            return System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
    }

    internal static class StringUtil
    {
        // "a;b;c" 에 group 이 있는가 (대소문자 무시)
        internal static bool HasGroup(string groups, string group)
        {
            if (string.IsNullOrEmpty(groups) || string.IsNullOrEmpty(group)) return false;
            foreach (string g in groups.Split(';'))
                if (g.Length > 0 && string.Equals(g, group, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 공백 · 기호를 뺀 이름 (메시지 "On" + 동작 이름, 만든 C# 클래스 식별자)
        internal static string Identifier(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? string.Empty)
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            return sb.ToString();
        }

        internal static bool WildcardMatch(string pattern, string text)
        {
            if (pattern.IndexOf('*') < 0) return string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase);
            // '*' 하나 이상 — 앞 · 뒤 조각이 맞으면
            string[] parts = pattern.Split('*');
            int at = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length == 0) continue;
                int found = text.IndexOf(p, at, StringComparison.OrdinalIgnoreCase);
                if (found < 0 || (i == 0 && found != 0)) return false;
                at = found + p.Length;
            }
            string last = parts[parts.Length - 1];
            return last.Length == 0 || text.EndsWith(last, StringComparison.OrdinalIgnoreCase);
        }
    }
}
