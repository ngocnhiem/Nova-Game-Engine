using System;
using System.Collections.Generic;
using System.Text.Json;

namespace NovaEngine.InputSystem
{
    // MonoBehaviour · ScriptableObject 필드의 InputAction · InputActionReference (Inspector 값 JSON)
    //  InputAction: {"name","type","id","expectedControlType","processors","interactions","initialStateCheck","bindings":[…]} — .inputactions 의 동작 + 바인딩과 같은 칸
    //  InputActionReference: {"asset": "Assets/X.inputactions", "action": "<동작 id>"}
    internal static class InputActionFieldJson
    {
        internal static void Write(Utf8JsonWriter w, InputAction a, string fieldName)
        {
            if (a == null) { w.WriteNullValue(); return; }
            w.WriteStartObject();
            w.WriteString("name", a.name ?? fieldName ?? string.Empty);
            w.WriteString("type", a.type.ToString());
            w.WriteString("id", a.idString);
            w.WriteString("expectedControlType", a.expectedControlType ?? string.Empty);
            w.WriteString("processors", a.processors ?? string.Empty);
            w.WriteString("interactions", a.interactions ?? string.Empty);
            w.WriteBoolean("initialStateCheck", a.m_InitialStateCheck);
            w.WriteStartArray("bindings");
            foreach (var b in a.bindings)
            {
                w.WriteStartObject();
                w.WriteString("name", b.name ?? string.Empty);
                w.WriteString("id", b.m_Id ?? string.Empty);
                w.WriteString("path", b.path ?? string.Empty);
                w.WriteString("interactions", b.interactions ?? string.Empty);
                w.WriteString("processors", b.processors ?? string.Empty);
                w.WriteString("groups", b.groups ?? string.Empty);
                w.WriteBoolean("isComposite", b.isComposite);
                w.WriteBoolean("isPartOfComposite", b.isPartOfComposite);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

        internal static InputAction ReadAction(JsonElement e, string fieldName)
        {
            if (e.ValueKind != JsonValueKind.Object) return new InputAction(fieldName);
            string type = Str(e, "type");
            var t = type == "Button" ? InputActionType.Button : type == "PassThrough" ? InputActionType.PassThrough : InputActionType.Value;
            string name = Str(e, "name");
            var a = new InputAction(string.IsNullOrEmpty(name) ? fieldName : name, t, null, Str(e, "interactions"), Str(e, "processors"), Str(e, "expectedControlType"))
            {
                m_InitialStateCheck = Bool(e, "initialStateCheck"),
            };
            string id = Str(e, "id");
            if (!string.IsNullOrEmpty(id)) a.m_Id = id;
            var map = a.GetOrCreateActionMap();
            var list = new List<InputBinding>();
            if (e.TryGetProperty("bindings", out var bs) && bs.ValueKind == JsonValueKind.Array)
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
                        m_Action = a.name,
                    };
                    ib.isComposite = Bool(b, "isComposite");
                    ib.isPartOfComposite = Bool(b, "isPartOfComposite");
                    ib.EnsureId();
                    list.Add(ib);
                }
            map.m_Bindings = list.ToArray();
            map.MarkDirty();
            return a;
        }

        internal static InputActionReference ReadReference(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            string path = Str(e, "asset"), id = Str(e, "action");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(id)) return null;
            var asset = InputActionAssetCache.Load(path);
            if (asset == null) return null;
            var r = ScriptableObject.CreateInstance<InputActionReference>();
            r.m_Asset = asset;
            r.m_ActionId = id;
            var act = r.action;
            r.name = act != null ? (act.actionMap != null ? act.actionMap.name + "/" : "") + act.name : id;
            return r;
        }
    }
}
