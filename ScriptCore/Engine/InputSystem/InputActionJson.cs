using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NovaEngine.InputSystem
{
    // .inputactions JSON (Unity 와 같은 형식 — maps · actions · bindings · controlSchemes)
    internal static class InputActionJson
    {
        static readonly JsonDocumentOptions kOptions = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

        static string Str(JsonElement e, string key)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
            return null;
        }

        static bool Bool(JsonElement e, string key, bool def = false)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble() != 0;
                if (v.ValueKind == JsonValueKind.String) return string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }
            return def;
        }

        static InputActionType ParseType(string s)
        {
            if (string.IsNullOrEmpty(s)) return InputActionType.Value;
            if (int.TryParse(s, out int n)) return (InputActionType)Math.Clamp(n, 0, 2);
            if (s.Equals("Button", StringComparison.OrdinalIgnoreCase)) return InputActionType.Button;
            if (s.Equals("PassThrough", StringComparison.OrdinalIgnoreCase) || s.Equals("Pass Through", StringComparison.OrdinalIgnoreCase)) return InputActionType.PassThrough;
            return InputActionType.Value;
        }

        internal static void ParseAsset(string json, InputActionAsset asset)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json.TrimStart('﻿'), kOptions);
            var root = doc.RootElement;
            string n = Str(root, "name");
            if (!string.IsNullOrEmpty(n)) asset.name = n;
            var maps = new List<InputActionMap>();
            if (root.TryGetProperty("maps", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray()) maps.Add(ParseMap(m, asset));
            asset.m_ActionMaps = maps.ToArray();
            var schemes = new List<InputControlScheme>();
            if (root.TryGetProperty("controlSchemes", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var s in cs.EnumerateArray())
                {
                    var reqs = new List<InputControlScheme.DeviceRequirement>();
                    if (s.TryGetProperty("devices", out var ds) && ds.ValueKind == JsonValueKind.Array)
                        foreach (var d in ds.EnumerateArray())
                            reqs.Add(new InputControlScheme.DeviceRequirement { m_ControlPath = Str(d, "devicePath"), m_Optional = Bool(d, "isOptional"), m_OR = Bool(d, "isOR") });
                    schemes.Add(new InputControlScheme(Str(s, "name"), reqs, Str(s, "bindingGroup")));
                }
            asset.m_ControlSchemes = schemes.ToArray();
        }

        internal static InputActionMap[] ParseMaps(string json)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json.TrimStart('﻿'), kOptions);
            var list = new List<InputActionMap>();
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("maps", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray()) list.Add(ParseMap(m, null));
            else if (root.ValueKind == JsonValueKind.Object)
                list.Add(ParseMap(root, null));
            return list.ToArray();
        }

        static InputActionMap ParseMap(JsonElement m, InputActionAsset asset)
        {
            var map = new InputActionMap(Str(m, "name")) { m_Asset = asset };
            string id = Str(m, "id");
            if (!string.IsNullOrEmpty(id)) map.m_Id = id;
            var actions = new List<InputAction>();
            if (m.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
                foreach (var a in acts.EnumerateArray())
                {
                    var act = new InputAction(Str(a, "name"), ParseType(Str(a, "type")), null, Str(a, "interactions"), Str(a, "processors"), Str(a, "expectedControlType"))
                    {
                        m_InitialStateCheck = Bool(a, "initialStateCheck"),
                        m_ActionMap = map,
                    };
                    string aid = Str(a, "id");
                    if (!string.IsNullOrEmpty(aid)) act.m_Id = aid;
                    actions.Add(act);
                }
            map.m_Actions = actions.ToArray();
            var bindings = new List<InputBinding>();
            if (m.TryGetProperty("bindings", out var bs) && bs.ValueKind == JsonValueKind.Array)
                foreach (var b in bs.EnumerateArray())
                {
                    var ib = new InputBinding
                    {
                        m_Name = Str(b, "name"),
                        m_Id = Str(b, "id"),
                        m_Path = Str(b, "path"),
                        m_Interactions = Str(b, "interactions"),
                        m_Processors = Str(b, "processors"),
                        m_Groups = Str(b, "groups"),
                        m_Action = Str(b, "action"),
                    };
                    ib.isComposite = Bool(b, "isComposite");
                    ib.isPartOfComposite = Bool(b, "isPartOfComposite");
                    ib.EnsureId();
                    bindings.Add(ib);
                }
            map.m_Bindings = bindings.ToArray();
            return map;
        }

        static readonly JsonWriterOptions kWriter = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        internal static string AssetToJson(InputActionAsset asset)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, kWriter))
            {
                w.WriteStartObject();
                w.WriteString("name", asset.name ?? string.Empty);
                w.WriteStartArray("maps");
                foreach (var m in asset.m_ActionMaps) WriteMap(w, m);
                w.WriteEndArray();
                w.WriteStartArray("controlSchemes");
                foreach (var s in asset.m_ControlSchemes)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.name ?? string.Empty);
                    w.WriteString("bindingGroup", s.bindingGroup ?? string.Empty);
                    w.WriteStartArray("devices");
                    foreach (var d in s.deviceRequirements)
                    {
                        w.WriteStartObject();
                        w.WriteString("devicePath", d.controlPath ?? string.Empty);
                        w.WriteBoolean("isOptional", d.isOptional);
                        w.WriteBoolean("isOR", d.isOR);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        internal static string MapsToJson(IEnumerable<InputActionMap> maps)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, kWriter))
            {
                w.WriteStartObject();
                w.WriteStartArray("maps");
                foreach (var m in maps) WriteMap(w, m);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static void WriteMap(Utf8JsonWriter w, InputActionMap m)
        {
            w.WriteStartObject();
            w.WriteString("name", m.name ?? string.Empty);
            w.WriteString("id", m.idString);
            w.WriteStartArray("actions");
            foreach (var a in m.m_Actions)
            {
                w.WriteStartObject();
                w.WriteString("name", a.name ?? string.Empty);
                w.WriteString("type", a.type.ToString());
                w.WriteString("id", a.idString);
                w.WriteString("expectedControlType", a.expectedControlType ?? string.Empty);
                w.WriteString("processors", a.processors ?? string.Empty);
                w.WriteString("interactions", a.interactions ?? string.Empty);
                w.WriteBoolean("initialStateCheck", a.m_InitialStateCheck || a.type == InputActionType.Value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("bindings");
            foreach (var b in m.m_Bindings)
            {
                w.WriteStartObject();
                w.WriteString("name", b.name ?? string.Empty);
                w.WriteString("id", b.m_Id ?? Guid.NewGuid().ToString());
                w.WriteString("path", b.path ?? string.Empty);
                w.WriteString("interactions", b.interactions ?? string.Empty);
                w.WriteString("processors", b.processors ?? string.Empty);
                w.WriteString("groups", b.groups ?? string.Empty);
                w.WriteString("action", b.action ?? string.Empty);
                w.WriteBoolean("isComposite", b.isComposite);
                w.WriteBoolean("isPartOfComposite", b.isPartOfComposite);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
    }
}
