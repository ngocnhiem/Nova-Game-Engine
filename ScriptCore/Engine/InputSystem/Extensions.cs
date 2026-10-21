using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using NovaEngine.InputSystem.Controls;
using NovaEngine.InputSystem.LowLevel;
using NovaEngine.InputSystem.Utilities;

namespace NovaEngine.InputSystem
{
    /// <summary>Unity 의 InputActionSetupExtensions — 코드로 맵 · 동작 · 바인딩 · 스킴 만들기</summary>
    public static class InputActionSetupExtensions
    {
        public static InputActionMap AddActionMap(this InputActionAsset asset, string name)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (asset.FindActionMap(name) != null) throw new InvalidOperationException($"An action map called '{name}' already exists in the asset");
            var map = new InputActionMap(name);
            asset.AddActionMap(map);
            return map;
        }

        public static void AddActionMap(this InputActionAsset asset, InputActionMap map)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (map.m_Asset != null) throw new InvalidOperationException($"Cannot add map '{map}' to asset '{asset}' as it has already been added to asset '{map.m_Asset}'");
            map.m_Asset = asset;
            var arr = asset.m_ActionMaps;
            Array.Resize(ref arr, arr.Length + 1);
            arr[arr.Length - 1] = map;
            asset.m_ActionMaps = arr;
        }

        public static void RemoveActionMap(this InputActionAsset asset, InputActionMap map)
        {
            if (asset == null || map == null || map.m_Asset != asset) return;
            map.Disable();
            var l = new List<InputActionMap>(asset.m_ActionMaps);
            l.Remove(map);
            asset.m_ActionMaps = l.ToArray();
            map.m_Asset = null;
        }

        public static void RemoveActionMap(this InputActionAsset asset, string nameOrId) => RemoveActionMap(asset, asset?.FindActionMap(nameOrId));

        public static InputAction AddAction(this InputActionMap map, string name, InputActionType type = default, string binding = null, string interactions = null, string processors = null, string groups = null, string expectedControlLayout = null)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Action must have name", nameof(name));
            if (map.enabled) throw new InvalidOperationException($"Cannot add action '{name}' to map '{map}' while it the map is enabled");
            if (map.FindAction(name) != null) throw new InvalidOperationException($"Cannot add action with duplicate name '{name}' to set '{map.name}'");
            var action = new InputAction(name, type, null, interactions, processors, expectedControlLayout) { m_ActionMap = map };
            var arr = map.m_Actions;
            Array.Resize(ref arr, arr.Length + 1);
            arr[arr.Length - 1] = action;
            map.m_Actions = arr;
            if (!string.IsNullOrEmpty(binding))
                action.AddBinding(binding, groups: groups);
            map.MarkDirty();
            return action;
        }

        public static void RemoveAction(this InputAction action)
        {
            var map = action?.m_ActionMap;
            if (map == null || map.m_IsSingleton) return;
            action.Disable();
            var keep = new List<InputBinding>();
            for (int i = 0; i < map.m_Bindings.Length; i++)
                if (map.FindActionForBinding(i) != action) keep.Add(map.m_Bindings[i]);
            map.m_Bindings = keep.ToArray();
            var acts = new List<InputAction>(map.m_Actions);
            acts.Remove(action);
            map.m_Actions = acts.ToArray();
            action.m_ActionMap = null;
            map.MarkDirty();
        }

        public static void RemoveAction(this InputActionAsset asset, string nameOrId) => asset?.FindAction(nameOrId)?.RemoveAction();

        public static void Rename(this InputAction action, string newName)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var map = action.m_ActionMap;
            if (map != null && !map.m_IsSingleton)
                for (int i = 0; i < map.m_Bindings.Length; i++)
                    if (!map.m_Bindings[i].isPartOfComposite && string.Equals(map.m_Bindings[i].m_Action, action.name, StringComparison.OrdinalIgnoreCase))
                        map.m_Bindings[i].m_Action = newName;
            action.m_Name = newName;
        }

        public static void Rename(this InputActionMap map, string newName) { if (map != null) map.m_Name = newName; }

        public static BindingSyntax AddBinding(this InputAction action, string path, string interactions = null, string processors = null, string groups = null)
            => AddBinding(action, new InputBinding(path, action?.name, groups, processors, interactions));

        public static BindingSyntax AddBinding(this InputAction action, InputControl control) => AddBinding(action, control.path);

        public static BindingSyntax AddBinding(this InputAction action, InputBinding binding = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var map = action.GetOrCreateActionMap();
            binding.m_Action = action.name;
            binding.EnsureId();
            // 맵 안에서는 같은 동작의 마지막 바인딩 뒤에 (Unity 와 같은 순서)
            int insert = map.m_Bindings.Length;
            var idx = map.BindingIndicesOf(action);
            if (idx.Count > 0) insert = idx[idx.Count - 1] + 1;
            map.InsertBinding(insert, binding);
            return new BindingSyntax(map, insert, action);
        }

        public static BindingSyntax AddBinding(this InputActionMap map, string path, InputAction action = null, string interactions = null, string processors = null, string groups = null)
        {
            var b = new InputBinding(path, action?.name, groups, processors, interactions);
            map.AppendBinding(b);
            return new BindingSyntax(map, map.m_Bindings.Length - 1, action);
        }

        public static BindingSyntax AddBinding(this InputActionMap map, InputBinding binding)
        {
            map.AppendBinding(binding);
            return new BindingSyntax(map, map.m_Bindings.Length - 1, null);
        }

        public static CompositeSyntax AddCompositeBinding(this InputAction action, string composite, string interactions = null, string processors = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var syntax = AddBinding(action, new InputBinding { m_Path = composite, m_Name = NameAndParameters.Parse(composite).name, m_Interactions = interactions, m_Processors = processors, isComposite = true });
            return new CompositeSyntax(syntax.m_Map, syntax.bindingIndexInMap, action);
        }

        public static BindingSyntax ChangeBinding(this InputAction action, int index)
        {
            var map = action.GetOrCreateActionMap();
            var idx = map.BindingIndicesOf(action);
            if (index < 0 || index >= idx.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return new BindingSyntax(map, idx[index], action);
        }

        public static BindingSyntax ChangeBinding(this InputAction action, string name)
        {
            var map = action.GetOrCreateActionMap();
            foreach (int i in map.BindingIndicesOf(action))
                if (string.Equals(map.m_Bindings[i].m_Name, name, StringComparison.OrdinalIgnoreCase)) return new BindingSyntax(map, i, action);
            return default;
        }

        public static BindingSyntax ChangeBinding(this InputAction action, InputBinding match)
        {
            var map = action.GetOrCreateActionMap();
            foreach (int i in map.BindingIndicesOf(action))
                if (match.Matches(ref map.m_Bindings[i])) return new BindingSyntax(map, i, action);
            return default;
        }

        public static BindingSyntax ChangeBindingWithId(this InputAction action, string id) => ChangeBinding(action, new InputBinding { m_Id = id });
        public static BindingSyntax ChangeBindingWithId(this InputAction action, Guid id) => ChangeBinding(action, new InputBinding { m_Id = id.ToString() });
        public static BindingSyntax ChangeBindingWithGroup(this InputAction action, string group) => ChangeBinding(action, new InputBinding { m_Groups = group });
        public static BindingSyntax ChangeBindingWithPath(this InputAction action, string path) => ChangeBinding(action, new InputBinding { m_Path = path });

        public static BindingSyntax ChangeCompositeBinding(this InputAction action, string compositeName)
        {
            var map = action.GetOrCreateActionMap();
            foreach (int i in map.BindingIndicesOf(action))
            {
                var b = map.m_Bindings[i];
                if (b.isComposite && (string.Equals(b.m_Name, compositeName, StringComparison.OrdinalIgnoreCase) || string.Equals(b.GetNameOfComposite(), compositeName, StringComparison.OrdinalIgnoreCase)))
                    return new BindingSyntax(map, i, action);
            }
            return default;
        }

        public static ControlSchemeSyntax AddControlScheme(this InputActionAsset asset, string name)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (asset.FindControlSchemeIndex(name) >= 0) throw new InvalidOperationException($"Asset '{asset.name}' already has a control scheme called '{name}'");
            var arr = asset.m_ControlSchemes;
            Array.Resize(ref arr, arr.Length + 1);
            arr[arr.Length - 1] = new InputControlScheme(name);
            asset.m_ControlSchemes = arr;
            return new ControlSchemeSyntax(asset, arr.Length - 1);
        }

        public static void AddControlScheme(this InputActionAsset asset, InputControlScheme scheme)
        {
            var arr = asset.m_ControlSchemes;
            Array.Resize(ref arr, arr.Length + 1);
            arr[arr.Length - 1] = scheme;
            asset.m_ControlSchemes = arr;
        }

        public static void RemoveControlScheme(this InputActionAsset asset, string name)
        {
            var l = new List<InputControlScheme>(asset.m_ControlSchemes);
            l.RemoveAll(s => string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase));
            asset.m_ControlSchemes = l.ToArray();
        }

        public static InputControlScheme WithBindingGroup(this InputControlScheme scheme, string bindingGroup) { scheme.m_BindingGroup = bindingGroup; return scheme; }
        public static InputControlScheme WithRequiredDevice(this InputControlScheme scheme, string controlPath) => WithDevice(scheme, controlPath, false);
        public static InputControlScheme WithOptionalDevice(this InputControlScheme scheme, string controlPath) => WithDevice(scheme, controlPath, true);
        public static InputControlScheme WithRequiredDevice<TDevice>(this InputControlScheme scheme) where TDevice : InputDevice => WithDevice(scheme, "<" + typeof(TDevice).Name + ">", false);
        public static InputControlScheme WithOptionalDevice<TDevice>(this InputControlScheme scheme) where TDevice : InputDevice => WithDevice(scheme, "<" + typeof(TDevice).Name + ">", true);

        static InputControlScheme WithDevice(InputControlScheme scheme, string path, bool optional)
        {
            var l = new List<InputControlScheme.DeviceRequirement>(scheme.m_DeviceRequirements ?? Array.Empty<InputControlScheme.DeviceRequirement>())
            {
                new InputControlScheme.DeviceRequirement { m_ControlPath = path, m_Optional = optional }
            };
            scheme.m_DeviceRequirements = l.ToArray();
            return scheme;
        }

        /// <summary>Unity 의 BindingSyntax — AddBinding(...).WithGroup(...).WithInteraction(...)</summary>
        public struct BindingSyntax
        {
            internal readonly InputActionMap m_Map;
            internal readonly int m_Index;
            readonly InputAction m_Action;

            internal BindingSyntax(InputActionMap map, int index, InputAction action) { m_Map = map; m_Index = index; m_Action = action; }

            public bool valid => m_Map != null && m_Index >= 0 && m_Index < m_Map.m_Bindings.Length;
            internal int bindingIndexInMap => m_Index;
            public int bindingIndex
            {
                get
                {
                    if (!valid) return -1;
                    if (m_Action == null) return m_Index;
                    var idx = m_Map.BindingIndicesOf(m_Action);
                    return idx.IndexOf(m_Index);
                }
            }
            public InputBinding binding => valid ? m_Map.m_Bindings[m_Index] : default;

            ref InputBinding B => ref m_Map.m_Bindings[m_Index];
            BindingSyntax Dirty() { m_Map.MarkDirty(); return this; }

            public BindingSyntax WithName(string name) { B.m_Name = name; return Dirty(); }
            public BindingSyntax WithPath(string path) { B.m_Path = path; return Dirty(); }
            public BindingSyntax WithGroup(string group)
            {
                B.m_Groups = string.IsNullOrEmpty(B.m_Groups) ? group : B.m_Groups + InputBinding.Separator + group;
                return Dirty();
            }
            public BindingSyntax WithGroups(string groups) => WithGroup(groups);
            public BindingSyntax WithInteraction(string interaction)
            {
                B.m_Interactions = string.IsNullOrEmpty(B.m_Interactions) ? interaction : B.m_Interactions + "," + interaction;
                return Dirty();
            }
            public BindingSyntax WithInteractions(string interactions) => WithInteraction(interactions);
            public BindingSyntax WithInteraction<TInteraction>() where TInteraction : IInputInteraction => WithInteraction(InputSystem.NameOfInteraction(typeof(TInteraction)));
            public BindingSyntax WithProcessor(string processor)
            {
                B.m_Processors = string.IsNullOrEmpty(B.m_Processors) ? processor : B.m_Processors + "," + processor;
                return Dirty();
            }
            public BindingSyntax WithProcessors(string processors) => WithProcessor(processors);
            public BindingSyntax WithProcessor<TProcessor>() where TProcessor : InputProcessor => WithProcessor(InputSystem.NameOfProcessor(typeof(TProcessor)));
            public BindingSyntax Triggering(InputAction action)
            {
                B.m_Action = action?.name;
                return Dirty();
            }
            public BindingSyntax To(InputBinding binding)
            {
                binding.m_Action ??= B.m_Action;
                binding.m_Id ??= B.m_Id;
                B = binding;
                return Dirty();
            }
            public BindingSyntax NextBinding() => valid && m_Index + 1 < m_Map.m_Bindings.Length ? new BindingSyntax(m_Map, m_Index + 1, m_Action) : default;
            public BindingSyntax PreviousBinding() => valid && m_Index > 0 ? new BindingSyntax(m_Map, m_Index - 1, m_Action) : default;
            public BindingSyntax NextPartBinding(string partName)
            {
                for (int i = m_Index + 1; m_Map != null && i < m_Map.m_Bindings.Length && m_Map.m_Bindings[i].isPartOfComposite; i++)
                    if (string.Equals(m_Map.m_Bindings[i].m_Name, partName, StringComparison.OrdinalIgnoreCase)) return new BindingSyntax(m_Map, i, m_Action);
                return default;
            }
            public BindingSyntax InsertPartBinding(string partName, string path)
            {
                var b = new InputBinding(path, B.m_Action, null, null, null, partName) { isPartOfComposite = true };
                m_Map.InsertBinding(m_Index + 1, b);
                return new BindingSyntax(m_Map, m_Index + 1, m_Action);
            }
            public void Erase()
            {
                if (!valid) return;
                var l = new List<InputBinding>(m_Map.m_Bindings);
                bool composite = l[m_Index].isComposite;
                l.RemoveAt(m_Index);
                if (composite)
                    while (m_Index < l.Count && l[m_Index].isPartOfComposite) l.RemoveAt(m_Index);
                m_Map.m_Bindings = l.ToArray();
                m_Map.MarkDirty();
            }
        }

        /// <summary>Unity 의 CompositeSyntax — AddCompositeBinding("2DVector").With("Up", "&lt;Keyboard&gt;/w")</summary>
        public struct CompositeSyntax
        {
            readonly InputActionMap m_Map;
            readonly int m_Index;
            readonly InputAction m_Action;
            int m_Parts;

            internal CompositeSyntax(InputActionMap map, int index, InputAction action) { m_Map = map; m_Index = index; m_Action = action; m_Parts = 0; }

            public int bindingIndex
            {
                get
                {
                    if (m_Map == null) return -1;
                    return m_Action != null ? m_Map.BindingIndicesOf(m_Action).IndexOf(m_Index) : m_Index;
                }
            }

            public CompositeSyntax With(string name, string binding, string groups = null, string processors = null, string interactions = null)
            {
                var b = new InputBinding(binding, m_Map.m_Bindings[m_Index].m_Action, groups, processors, interactions, name) { isPartOfComposite = true };
                int at = m_Index + 1;
                while (at < m_Map.m_Bindings.Length && m_Map.m_Bindings[at].isPartOfComposite) at++;
                m_Map.InsertBinding(at, b);
                m_Parts++;
                return this;
            }
        }

        /// <summary>Unity 의 ControlSchemeSyntax — AddControlScheme("Gamepad").WithRequiredDevice("&lt;Gamepad&gt;")</summary>
        public struct ControlSchemeSyntax
        {
            readonly InputActionAsset m_Asset;
            readonly int m_Index;
            internal ControlSchemeSyntax(InputActionAsset asset, int index) { m_Asset = asset; m_Index = index; }

            public ControlSchemeSyntax WithBindingGroup(string bindingGroup) { m_Asset.m_ControlSchemes[m_Index].m_BindingGroup = bindingGroup; return this; }
            public ControlSchemeSyntax WithRequiredDevice(string controlPath) { m_Asset.m_ControlSchemes[m_Index] = WithDevice(m_Asset.m_ControlSchemes[m_Index], controlPath, false); return this; }
            public ControlSchemeSyntax WithOptionalDevice(string controlPath) { m_Asset.m_ControlSchemes[m_Index] = WithDevice(m_Asset.m_ControlSchemes[m_Index], controlPath, true); return this; }
            public ControlSchemeSyntax OrWithRequiredDevice(string controlPath)
            {
                var s = WithDevice(m_Asset.m_ControlSchemes[m_Index], controlPath, false);
                s.m_DeviceRequirements[s.m_DeviceRequirements.Length - 1].m_OR = true;
                m_Asset.m_ControlSchemes[m_Index] = s;
                return this;
            }
            public ControlSchemeSyntax WithRequiredDevice<TDevice>() where TDevice : InputDevice => WithRequiredDevice("<" + typeof(TDevice).Name + ">");
            public ControlSchemeSyntax WithOptionalDevice<TDevice>() where TDevice : InputDevice => WithOptionalDevice("<" + typeof(TDevice).Name + ">");
            public InputControlScheme Done() => m_Asset.m_ControlSchemes[m_Index];
        }
    }

    /// <summary>Unity 의 InputActionRebindingExtensions — 다시 묶기 (덮어쓰기 경로 · 저장 · 플레이어가 키를 눌러 고르기)</summary>
    public static class InputActionRebindingExtensions
    {
        // ---- 바인딩 찾기
        public static int GetBindingIndex(this InputAction action, InputBinding bindingMask)
        {
            var map = action.GetOrCreateActionMap();
            var idx = map.BindingIndicesOf(action);
            for (int i = 0; i < idx.Count; i++)
                if (bindingMask.Matches(ref map.m_Bindings[idx[i]])) return i;
            return -1;
        }

        public static int GetBindingIndex(this InputAction action, string group = null, string path = null)
            => GetBindingIndex(action, new InputBinding { m_Groups = group, m_Path = path });

        public static int GetBindingIndexForControl(this InputAction action, InputControl control)
        {
            var map = action.GetOrCreateActionMap();
            var idx = map.BindingIndicesOf(action);
            for (int i = 0; i < idx.Count; i++)
            {
                var b = map.m_Bindings[idx[i]];
                if (!b.isComposite && InputControlPath.Matches(b.effectivePath, control)) return i;
            }
            return -1;
        }

        // ---- 보여 줄 글자
        public static string GetBindingDisplayString(this InputAction action, InputBinding.DisplayStringOptions options = default, string group = null)
        {
            var map = action.GetOrCreateActionMap();
            var idx = map.BindingIndicesOf(action);
            var parts = new List<string>();
            for (int i = 0; i < idx.Count; i++)
            {
                var b = map.m_Bindings[idx[i]];
                if (b.isPartOfComposite) continue;
                if (group != null && !b.isComposite && !StringUtil.HasGroup(b.m_Groups, group)) continue;
                string s = GetBindingDisplayString(action, i, options, group);
                if (!string.IsNullOrEmpty(s) && !parts.Contains(s)) parts.Add(s);
            }
            return string.Join(" | ", parts);
        }

        public static string GetBindingDisplayString(this InputAction action, InputBinding bindingMask, InputBinding.DisplayStringOptions options = default)
        {
            int i = GetBindingIndex(action, bindingMask);
            return i >= 0 ? GetBindingDisplayString(action, i, options) : string.Empty;
        }

        public static string GetBindingDisplayString(this InputAction action, int bindingIndex, InputBinding.DisplayStringOptions options = default)
            => GetBindingDisplayString(action, bindingIndex, options, null);

        public static string GetBindingDisplayString(this InputAction action, int bindingIndex, out string deviceLayoutName, out string controlPath, InputBinding.DisplayStringOptions options = default)
        {
            var map = action.GetOrCreateActionMap();
            var idx = map.BindingIndicesOf(action);
            deviceLayoutName = controlPath = null;
            if (bindingIndex < 0 || bindingIndex >= idx.Count) throw new ArgumentOutOfRangeException(nameof(bindingIndex));
            var b = map.m_Bindings[idx[bindingIndex]];
            if (b.isComposite) return GetBindingDisplayString(action, bindingIndex, options, null);
            return b.ToDisplayString(out deviceLayoutName, out controlPath, options, null);
        }

        static string GetBindingDisplayString(InputAction action, int bindingIndex, InputBinding.DisplayStringOptions options, string group)
        {
            var map = action.GetOrCreateActionMap();
            var idx = map.BindingIndicesOf(action);
            if (bindingIndex < 0 || bindingIndex >= idx.Count) throw new ArgumentOutOfRangeException(nameof(bindingIndex));
            int mi = idx[bindingIndex];
            var b = map.m_Bindings[mi];
            if (!b.isComposite) return b.ToDisplayString(options);
            // 합성: Unity 의 표시 형식 (2DVector = {up}/{left}/{down}/{right})
            string name = (b.GetNameOfComposite() ?? string.Empty).ToLowerInvariant();
            string format;
            switch (name)
            {
                case "2dvector": case "dpad": format = "{up}/{left}/{down}/{right}"; break;
                case "3dvector": format = "{up}+{down}/{left}+{right}/{forward}+{backward}"; break;
                case "1daxis": case "axis": format = "{negative}/{positive}"; break;
                case "buttonwithonemodifier": format = "{modifier}+{button}"; break;
                case "buttonwithtwomodifiers": format = "{modifier1}+{modifier2}+{button}"; break;
                case "onemodifier": format = "{modifier}+{binding}"; break;
                case "twomodifiers": format = "{modifier1}+{modifier2}+{binding}"; break;
                default: format = null; break;
            }
            var partText = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            for (int i = mi + 1; i < map.m_Bindings.Length && map.m_Bindings[i].isPartOfComposite; i++)
            {
                var p = map.m_Bindings[i];
                if (group != null && !StringUtil.HasGroup(p.m_Groups, group)) continue;
                string pn = p.m_Name ?? string.Empty;
                if (!partText.TryGetValue(pn, out var l)) { partText[pn] = l = new List<string>(); order.Add(pn); }
                string s = p.ToDisplayString(options | InputBinding.DisplayStringOptions.DontIncludeInteractions);
                if (!l.Contains(s)) l.Add(s);
            }
            if (format == null)
            {
                var all = new List<string>();
                foreach (string pn in order) all.Add(string.Join("|", partText[pn]));
                return string.Join("/", all);
            }
            var sb = new StringBuilder();
            int at = 0;
            while (at < format.Length)
            {
                if (format[at] == '{')
                {
                    int end = format.IndexOf('}', at);
                    string key = format.Substring(at + 1, end - at - 1);
                    if (partText.TryGetValue(key, out var l)) sb.Append(string.Join("|", l));
                    at = end + 1;
                }
                else sb.Append(format[at++]);
            }
            return sb.ToString();
        }

        // ---- 덮어쓰기 (다시 묶기)
        public static void ApplyBindingOverride(this InputAction action, string newPath, string group = null, string path = null)
            => ApplyBindingOverride(action, new InputBinding { m_OverridePath = newPath, m_Groups = group, m_Path = path });

        public static void ApplyBindingOverride(this InputAction action, InputBinding bindingOverride)
        {
            var map = action.GetOrCreateActionMap();
            var mask = new InputBinding { m_Groups = bindingOverride.m_Groups, m_Path = bindingOverride.m_Path, m_Name = bindingOverride.m_Name, m_Id = bindingOverride.m_Id };
            foreach (int i in map.BindingIndicesOf(action))
            {
                if (map.m_Bindings[i].isComposite) continue;
                if (!mask.Matches(ref map.m_Bindings[i])) continue;
                Override(ref map.m_Bindings[i], bindingOverride);
            }
            map.MarkDirty();
        }

        public static void ApplyBindingOverride(this InputAction action, int bindingIndex, InputBinding bindingOverride)
        {
            var map = action.GetOrCreateActionMap();
            var idx = map.BindingIndicesOf(action);
            if (bindingIndex < 0 || bindingIndex >= idx.Count) throw new ArgumentOutOfRangeException(nameof(bindingIndex));
            Override(ref map.m_Bindings[idx[bindingIndex]], bindingOverride);
            map.MarkDirty();
        }

        public static void ApplyBindingOverride(this InputAction action, int bindingIndex, string path)
            => ApplyBindingOverride(action, bindingIndex, new InputBinding { m_OverridePath = path });

        public static int ApplyBindingOverride(this InputActionMap actionMap, InputBinding bindingOverride)
        {
            int n = 0;
            var mask = new InputBinding { m_Groups = bindingOverride.m_Groups, m_Path = bindingOverride.m_Path, m_Action = bindingOverride.m_Action, m_Name = bindingOverride.m_Name, m_Id = bindingOverride.m_Id };
            for (int i = 0; i < actionMap.m_Bindings.Length; i++)
                if (mask.Matches(ref actionMap.m_Bindings[i])) { Override(ref actionMap.m_Bindings[i], bindingOverride); n++; }
            if (n > 0) actionMap.MarkDirty();
            return n;
        }

        public static void ApplyBindingOverride(this InputActionMap actionMap, int bindingIndex, InputBinding bindingOverride)
        {
            Override(ref actionMap.m_Bindings[bindingIndex], bindingOverride);
            actionMap.MarkDirty();
        }

        public static void ApplyBindingOverrides(this InputActionMap actionMap, IEnumerable<InputBinding> overrides)
        {
            foreach (var o in overrides) ApplyBindingOverride(actionMap, o);
        }

        static void Override(ref InputBinding b, InputBinding o)
        {
            b.m_OverridePath = o.m_OverridePath;
            b.m_OverrideInteractions = o.m_OverrideInteractions;
            b.m_OverrideProcessors = o.m_OverrideProcessors;
        }

        public static void RemoveBindingOverride(this InputAction action, int bindingIndex)
            => ApplyBindingOverride(action, bindingIndex, default(InputBinding));

        public static void RemoveBindingOverride(this InputAction action, InputBinding bindingMask)
        {
            var map = action.GetOrCreateActionMap();
            foreach (int i in map.BindingIndicesOf(action))
                if (bindingMask.Matches(ref map.m_Bindings[i])) Override(ref map.m_Bindings[i], default);
            map.MarkDirty();
        }

        public static void RemoveBindingOverrides(this InputActionMap actionMap, IEnumerable<InputBinding> overrides)
        {
            foreach (var o in overrides)
            {
                var mask = new InputBinding { m_Groups = o.m_Groups, m_Path = o.m_Path, m_Action = o.m_Action, m_Id = o.m_Id };
                for (int i = 0; i < actionMap.m_Bindings.Length; i++)
                    if (mask.Matches(ref actionMap.m_Bindings[i])) Override(ref actionMap.m_Bindings[i], default);
            }
            actionMap.MarkDirty();
        }

        public static void RemoveAllBindingOverrides(this InputAction action)
        {
            var map = action.GetOrCreateActionMap();
            foreach (int i in map.BindingIndicesOf(action)) Override(ref map.m_Bindings[i], default);
            map.MarkDirty();
        }

        public static void RemoveAllBindingOverrides(this IInputActionCollection2 actions)
        {
            foreach (var map in MapsOf(actions))
            {
                for (int i = 0; i < map.m_Bindings.Length; i++) Override(ref map.m_Bindings[i], default);
                map.MarkDirty();
            }
        }

        static IEnumerable<InputActionMap> MapsOf(IInputActionCollection2 actions)
        {
            if (actions is InputActionAsset a) { foreach (var m in a.m_ActionMaps) yield return m; }
            else if (actions is InputActionMap m) yield return m;
            else
            {
                var seen = new HashSet<InputActionMap>();
                foreach (var act in actions)
                {
                    var map = act.GetOrCreateActionMap();
                    if (seen.Add(map)) yield return map;
                }
            }
        }

        // ---- 저장 · 읽기 (Unity 와 같은 JSON: {"bindings":[{"action":"Player/Jump","id":"…","path":"<Keyboard>/k","interactions":"","processors":""}]})
        public static string SaveBindingOverridesAsJson(this IInputActionCollection2 actions)
        {
            var list = new List<(string action, InputBinding b)>();
            foreach (var map in MapsOf(actions))
                for (int i = 0; i < map.m_Bindings.Length; i++)
                    if (map.m_Bindings[i].hasOverrides)
                    {
                        var act = map.FindActionForBinding(i);
                        list.Add(((map.m_IsSingleton ? "" : map.name + "/") + act?.name, map.m_Bindings[i]));
                    }
            return WriteOverrides(list);
        }

        public static string SaveBindingOverridesAsJson(this InputAction action)
        {
            var map = action.GetOrCreateActionMap();
            var list = new List<(string, InputBinding)>();
            foreach (int i in map.BindingIndicesOf(action))
                if (map.m_Bindings[i].hasOverrides)
                    list.Add(((map.m_IsSingleton ? "" : map.name + "/") + action.name, map.m_Bindings[i]));
            return WriteOverrides(list);
        }

        static string WriteOverrides(List<(string action, InputBinding b)> list)
        {
            using var ms = new MemoryStream();
            // Unity 와 같이 "<Keyboard>/k" 를 그대로 (꺾쇠를 유니코드 탈출 문자로 바꾸지 않는다)
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteStartArray("bindings");
                foreach (var (action, b) in list)
                {
                    w.WriteStartObject();
                    w.WriteString("action", action ?? string.Empty);
                    w.WriteString("id", b.m_Id ?? string.Empty);
                    w.WriteString("path", b.m_OverridePath ?? string.Empty);
                    w.WriteString("interactions", b.m_OverrideInteractions ?? string.Empty);
                    w.WriteString("processors", b.m_OverrideProcessors ?? string.Empty);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public static void LoadBindingOverridesFromJson(this IInputActionCollection2 actions, string json, bool removeExisting = true)
        {
            if (removeExisting) RemoveAllBindingOverrides(actions);
            ApplyOverridesJson(MapsOf(actions), json);
        }

        public static void LoadBindingOverridesFromJson(this InputAction action, string json, bool removeExisting = true)
        {
            if (removeExisting) RemoveAllBindingOverrides(action);
            ApplyOverridesJson(new[] { action.GetOrCreateActionMap() }, json);
        }

        static void ApplyOverridesJson(IEnumerable<InputActionMap> maps, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("bindings", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            var mapList = new List<InputActionMap>(maps);
            foreach (var e in arr.EnumerateArray())
            {
                string id = e.TryGetProperty("id", out var v) ? v.GetString() : null;
                string path = e.TryGetProperty("path", out v) ? v.GetString() : null;
                string inter = e.TryGetProperty("interactions", out v) ? v.GetString() : null;
                string proc = e.TryGetProperty("processors", out v) ? v.GetString() : null;
                var o = new InputBinding { m_OverridePath = string.IsNullOrEmpty(path) ? null : path, m_OverrideInteractions = string.IsNullOrEmpty(inter) ? null : inter, m_OverrideProcessors = string.IsNullOrEmpty(proc) ? null : proc };
                foreach (var map in mapList)
                {
                    bool done = false;
                    for (int i = 0; i < map.m_Bindings.Length; i++)
                        if (!string.IsNullOrEmpty(id) && string.Equals(map.m_Bindings[i].m_Id, id, StringComparison.OrdinalIgnoreCase))
                        {
                            Override(ref map.m_Bindings[i], o);
                            map.MarkDirty();
                            done = true;
                            break;
                        }
                    if (done) break;
                }
            }
        }

        // ---- 플레이어가 고르기 (Unity 의 PerformInteractiveRebinding)
        public static RebindingOperation PerformInteractiveRebinding(this InputAction action, int bindingIndex = -1)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var op = new RebindingOperation().WithAction(action);
            if (bindingIndex >= 0) op.WithTargetBinding(bindingIndex);
            if (!string.IsNullOrEmpty(action.expectedControlType)) op.WithExpectedControlType(action.expectedControlType);
            return op;
        }

        /// <summary>Unity 의 RebindingOperation — Start() 뒤 다음 입력으로 바인딩을 바꾼다 (동작은 꺼 두어야 한다)</summary>
        public sealed class RebindingOperation : IDisposable
        {
            public const float kDefaultMagnitudeThreshold = 0.2f;

            InputAction m_Action;
            int m_TargetBinding = -1;
            InputBinding? m_BindingMask;
            string m_ExpectedType;
            float m_Threshold = kDefaultMagnitudeThreshold;
            bool m_IgnoreNoisy = true;
            bool m_Generalize = true;
            bool m_AddNew;
            readonly List<string> m_Include = new List<string>();
            readonly List<string> m_Exclude = new List<string>();
            readonly List<string> m_Cancel = new List<string>();
            float m_Timeout;
            float m_WaitSeconds;
            double m_StartTime, m_LastMatchTime = -1;
            Action<RebindingOperation> m_OnComplete, m_OnCancel, m_OnPotentialMatch;
            Func<InputControl, string> m_OnGeneratePath;
            Func<InputControl, InputEventPtr, float> m_OnComputeScore;
            Action<RebindingOperation, string> m_OnApplyBinding;
            readonly List<InputControl> m_Candidates = new List<InputControl>();
            readonly List<float> m_Scores = new List<float>();
            InputControl m_Selected;
            bool m_Started, m_Completed, m_Canceled;

            public InputAction action => m_Action;
            public InputBinding? bindingMask => m_BindingMask;
            public string expectedControlType => m_ExpectedType;
            public float magnitudeThreshold => m_Threshold;
            public ReadOnlyArray<InputControl> candidates => m_Candidates.ToArray();
            public ReadOnlyArray<float> scores => m_Scores.ToArray();
            public ReadOnlyArray<float> magnitudes => m_Scores.ToArray();
            public InputControl selectedControl => m_Selected;
            public bool started => m_Started;
            public bool completed => m_Completed;
            public bool canceled => m_Canceled;
            public double startTime => m_StartTime;
            public float timeout => m_Timeout;

            public RebindingOperation WithAction(InputAction action) { ThrowIfStarted(); m_Action = action; return this; }
            public RebindingOperation WithTargetBinding(int bindingIndex) { m_TargetBinding = bindingIndex; return this; }
            public RebindingOperation WithBindingMask(InputBinding? bindingMask) { m_BindingMask = bindingMask; return this; }
            public RebindingOperation WithBindingGroup(string group) => WithBindingMask(new InputBinding { m_Groups = group });
            public RebindingOperation WithExpectedControlType(string layoutName) { m_ExpectedType = layoutName; return this; }
            public RebindingOperation WithExpectedControlType(Type type) { m_ExpectedType = type == typeof(Vector2) ? "Vector2" : type == typeof(float) ? "Axis" : type?.Name; return this; }
            public RebindingOperation WithExpectedControlType<TControl>() where TControl : InputControl => WithExpectedControlType(typeof(TControl).Name.Replace("Control", ""));
            public RebindingOperation WithMagnitudeHavingToBeGreaterThan(float magnitude) { m_Threshold = magnitude; return this; }
            public RebindingOperation WithoutIgnoringNoisyControls() { m_IgnoreNoisy = false; return this; }
            public RebindingOperation WithoutGeneralizingPathOfSelectedControl() { m_Generalize = false; return this; }
            public RebindingOperation WithRebindAddingNewBinding(string group = null) { m_AddNew = true; if (group != null) m_BindingMask = new InputBinding { m_Groups = group }; return this; }
            public RebindingOperation WithControlsHavingToMatchPath(string path) { m_Include.Add(path); return this; }
            public RebindingOperation WithControlsExcluding(string path) { m_Exclude.Add(path); return this; }
            public RebindingOperation WithCancelingThrough(string binding) { m_Cancel.Add(binding); return this; }
            public RebindingOperation WithCancelingThrough(InputControl control) { m_Cancel.Add(control.path); return this; }
            public RebindingOperation WithMatchingEventsBeingSuppressed(bool value = true) => this;
            public RebindingOperation WithTimeout(float timeInSeconds) { m_Timeout = timeInSeconds; return this; }
            public RebindingOperation OnComplete(Action<RebindingOperation> callback) { m_OnComplete = callback; return this; }
            public RebindingOperation OnCancel(Action<RebindingOperation> callback) { m_OnCancel = callback; return this; }
            public RebindingOperation OnPotentialMatch(Action<RebindingOperation> callback) { m_OnPotentialMatch = callback; return this; }
            public RebindingOperation OnGeneratePath(Func<InputControl, string> callback) { m_OnGeneratePath = callback; return this; }
            public RebindingOperation OnComputeScore(Func<InputControl, InputEventPtr, float> callback) { m_OnComputeScore = callback; return this; }
            public RebindingOperation OnApplyBinding(Action<RebindingOperation, string> callback) { m_OnApplyBinding = callback; return this; }
            public RebindingOperation OnMatchWaitForAnother(float seconds) { m_WaitSeconds = seconds; return this; }

            void ThrowIfStarted()
            {
                if (m_Started && !m_Completed && !m_Canceled) throw new InvalidOperationException("Operation has already been started");
            }

            public RebindingOperation Start()
            {
                if (m_Action == null) throw new InvalidOperationException("Must set action to rebind");
                if (m_Action.enabled) throw new InvalidOperationException($"Cannot rebind action '{m_Action}' while it is enabled");
                if (m_TargetBinding < 0 && !m_AddNew)
                {
                    var idx = m_Action.GetOrCreateActionMap().BindingIndicesOf(m_Action);
                    for (int i = 0; i < idx.Count; i++)
                    {
                        var b = m_Action.m_ActionMap.m_Bindings[idx[i]];
                        if (b.isComposite) continue;
                        if (m_BindingMask.HasValue && !m_BindingMask.Value.Matches(ref b)) continue;
                        m_TargetBinding = i;
                        break;
                    }
                }
                m_Started = true;
                m_Completed = m_Canceled = false;
                m_Candidates.Clear();
                m_Scores.Clear();
                m_Selected = null;
                m_StartTime = InputSystem.CurrentTime;
                m_LastMatchTime = -1;
                InputSystem.s_Rebinds.Add(this);
                return this;
            }

            public void Cancel()
            {
                if (!m_Started || m_Completed || m_Canceled) return;
                m_Canceled = true;
                InputSystem.s_Rebinds.Remove(this);
                try { m_OnCancel?.Invoke(this); } catch (Exception e) { Debug.LogException(e); }
            }

            public void Complete()
            {
                if (!m_Started || m_Completed || m_Canceled) return;
                m_Selected = m_Candidates.Count > 0 ? m_Candidates[0] : null;
                if (m_Selected == null) { Cancel(); return; }
                string path = m_OnGeneratePath?.Invoke(m_Selected) ?? GeneratePath(m_Selected, m_Generalize);
                if (m_OnApplyBinding != null) m_OnApplyBinding(this, path);
                else if (m_AddNew)
                {
                    var syntax = m_Action.AddBinding(path);
                    if (m_BindingMask.HasValue && !string.IsNullOrEmpty(m_BindingMask.Value.m_Groups)) syntax.WithGroups(m_BindingMask.Value.m_Groups);
                }
                else if (m_TargetBinding >= 0)
                    m_Action.ApplyBindingOverride(m_TargetBinding, path);
                m_Completed = true;
                InputSystem.s_Rebinds.Remove(this);
                try { m_OnComplete?.Invoke(this); } catch (Exception e) { Debug.LogException(e); }
            }

            public RebindingOperation AddCandidate(InputControl control, float score, float magnitude = -1)
            {
                int at = 0;
                while (at < m_Scores.Count && m_Scores[at] >= score) at++;
                m_Candidates.Insert(at, control);
                m_Scores.Insert(at, score);
                return this;
            }

            public RebindingOperation RemoveCandidate(InputControl control)
            {
                int i = m_Candidates.IndexOf(control);
                if (i >= 0) { m_Candidates.RemoveAt(i); m_Scores.RemoveAt(i); }
                return this;
            }

            public RebindingOperation Reset()
            {
                Cancel();
                m_Started = m_Completed = m_Canceled = false;
                m_Candidates.Clear();
                m_Scores.Clear();
                m_Selected = null;
                return this;
            }

            public void Dispose()
            {
                if (m_Started && !m_Completed && !m_Canceled) Cancel();
                InputSystem.s_Rebinds.Remove(this);
            }

            // "<Gamepad>/buttonSouth" (패드 종류를 가리지 않게 기반 레이아웃으로), "<Keyboard>/k"
            internal static string GeneratePath(InputControl control, bool generalize)
            {
                var device = control.device;
                string layout = device.layout;
                if (generalize)
                {
                    if (device is Gamepad) layout = "Gamepad";
                    else if (device is Keyboard) layout = "Keyboard";
                    else if (device is Mouse) layout = "Mouse";
                    else if (device is Touchscreen) layout = "Touchscreen";
                }
                string full = control.path;
                string rel = full.Substring(device.path.Length).TrimStart('/');
                return "<" + layout + ">/" + rel;
            }

            bool Matches(List<string> paths, InputControl c)
            {
                foreach (string p in paths)
                    if (InputControlPath.Matches(p, c) || InputControlPath.MatchesPrefix(p, c)) return true;
                return false;
            }

            bool TypeOk(InputControl c)
            {
                if (string.IsNullOrEmpty(m_ExpectedType)) return !(c is InputDevice);
                if (string.Equals(m_ExpectedType, "Button", StringComparison.OrdinalIgnoreCase)) return c is ButtonControl;
                var want = ValueUtil.KindForControlType(m_ExpectedType);
                if (want == ValueKind.None) return c.IsBasedOnLayout(m_ExpectedType);
                return ValueUtil.KindOfControl(c) == want;
            }

            // InputSystem.Update 가 프레임마다 (장치 갱신 뒤)
            internal void Process(double now)
            {
                if (!m_Started || m_Completed || m_Canceled) return;
                if (m_Timeout > 0 && now - m_StartTime >= m_Timeout)
                {
                    if (m_Candidates.Count > 0) Complete(); else Cancel();
                    return;
                }
                bool found = false;
                foreach (var device in InputSystem.s_Devices)
                    foreach (var c in device.allControls)
                    {
                        if (c.m_Children.Length > 0 && !(c is Vector2Control) && !(c is TouchControl)) continue;
                        if (c is Vector2Control && c.m_Parent is Vector2Control) continue;
                        float mag = c.EvaluateMagnitude();
                        if (mag < m_Threshold) continue;
                        float prev = PrevMagnitude(c);
                        if (prev >= m_Threshold) continue;   // 이번 프레임에 넘었을 때만
                        if (m_Cancel.Count > 0 && Matches(m_Cancel, c)) { Cancel(); return; }
                        if (m_IgnoreNoisy && c.noisy) continue;
                        if (c is AnyKeyControl || (c.synthetic && !(c.m_Parent is StickControl) && !(c.m_Parent is DpadControl))) continue;
                        // Vector2 를 바라면 스틱 · 십자키 전체
                        InputControl pick = c;
                        if (!TypeOk(pick) && pick.m_Parent is Vector2Control v2 && TypeOk(v2)) pick = v2;
                        if (!TypeOk(pick)) continue;
                        if (m_Include.Count > 0 && !Matches(m_Include, pick)) continue;
                        if (m_Exclude.Count > 0 && Matches(m_Exclude, pick)) continue;
                        if (m_Candidates.Contains(pick)) continue;
                        float score = m_OnComputeScore != null ? m_OnComputeScore(pick, default) : mag;
                        AddCandidate(pick, score, mag);
                        found = true;
                    }
                if (found)
                {
                    m_LastMatchTime = now;
                    try { m_OnPotentialMatch?.Invoke(this); } catch (Exception e) { Debug.LogException(e); }
                    if (m_WaitSeconds <= 0f) { Complete(); return; }
                }
                if (m_LastMatchTime >= 0 && m_WaitSeconds > 0f && now - m_LastMatchTime >= m_WaitSeconds) Complete();
            }

            static float PrevMagnitude(InputControl c)
            {
                switch (c)
                {
                    case InputControl<float> f: return Math.Min(1f, Math.Abs(f.ReadValueFromPreviousFrame()));
                    case InputControl<Vector2> v: return v.ReadValueFromPreviousFrame().magnitude;
                    case TouchControl t: return t.press.ReadValueFromPreviousFrame();
                }
                return 0f;
            }
        }
    }

}

namespace NovaEngine.InputSystem.LowLevel
{
    /// <summary>Unity 의 InputEventPtr 자리 (OnComputeScore 인자 — NOVA 는 이벤트 대신 프레임 상태)</summary>
    public struct InputEventPtr
    {
        public bool valid => false;
        public double time => InputSystem.CurrentTime;
    }
}
