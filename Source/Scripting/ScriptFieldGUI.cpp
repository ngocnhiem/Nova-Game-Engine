#include "pch.h"
#include "ScriptFieldGUI.h"
#include "ScriptableAssets.h"
#include "UnityGUI.h"
#include "ObjectPicker.h"
#include "AudioClip.h"
#include "AudioManager.h"
#include "SelectionManager.h"
#include "InputActionsAsset.h"
#include <filesystem>

using json = nlohmann::json;

namespace
{
	std::string ObjectName(uint64 id, const char* noneText)
	{
		if (id == 0)
			return noneText;
		Scene* scene = SceneManager::GetI()->GetCurrentScene();
		GameObject* go = scene ? scene->FindByFileID(id) : nullptr;
		return go ? go->GetName() : std::string("Missing");
	}

	std::string Stem(const std::string& path)
	{
		return wstring_to_string(std::filesystem::path(string_to_wstring(path)).stem().wstring());
	}

	// 에셋 경로 필드 (AudioClip · ScriptableObject): ⊙ = 고르기 창, Project 에서 끌어 놓기, 두 번 클릭 = Project 에서 고르기
	bool AssetRefField(const char* label, json& v, const std::string& key, const char* icon, const std::string& typeName,
		const std::function<std::vector<std::string>()>& items, const std::function<bool(const std::string&)>& accept,
		const std::function<void(const std::string&)>& preview, int indent)
	{
		bool changed = false;
		const std::string path = v.is_string() ? v.get<std::string>() : std::string();
		std::string text = path.empty() ? "None (" + typeName + ")" : Stem(path);
		if (!path.empty() && !accept(path))
			text = "Missing (" + typeName + ")";
		ImVec2 fmin, fmax;
		const int pressed = UnityGUI::ObjectFieldButtons(label, text.c_str(), icon, nullptr, 0, &fmin, &fmax, indent);
		const ImVec2 after = ImGui::GetCursorScreenPos();
		if (pressed == -1)
		{
			ObjectPicker::Options opt;
			opt.TypeName = typeName;
			opt.Icon = icon;
			opt.Items = items();
			opt.Current = path;
			if (preview)
				opt.Preview = preview;
			ObjectPicker::Open(key, std::move(opt));
		}
		std::string picked;
		if (ObjectPicker::Poll(key, picked)) { v = picked; changed = true; }
		ImGui::SetCursorScreenPos(fmin);
		ImGui::InvisibleButton("##assetdrop", ImVec2((std::max)(1.0f, fmax.x - fmin.x - 22.0f), fmax.y - fmin.y));
		if (ImGui::IsItemHovered() && ImGui::IsMouseDoubleClicked(ImGuiMouseButton_Left) && !path.empty())
			SelectionManager::SetSelectedFile(PathManager::GetI()->GetMovePathW(string_to_wstring(path)));
		if (ImGui::BeginDragDropTarget())
		{
			if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload("ASSET_FILE"))
			{
				const std::string dropped(static_cast<const char*>(payload->Data));
				if (accept(dropped)) { v = dropped; changed = true; }
			}
			ImGui::EndDragDropTarget();
		}
		ImGui::SetCursorScreenPos(after);
		return changed;
	}

	bool Value(const ScriptEngine::FieldInfo& f, json& v, const char* label, const std::string& key, int indent);

	// InputActionReference: {asset, action} — 모든 .inputactions 의 동작 목록에서 고른다 (Unity 의 동작 고르기 창)
	bool ActionReferenceField(const char* label, json& v, int indent)
	{
		bool changed = false;
		const std::string asset = v.is_object() ? v.value("asset", std::string()) : std::string();
		const std::string id = v.is_object() ? v.value("action", std::string()) : std::string();
		std::string text = "None (Input Action Reference)";
		if (!asset.empty())
		{
			InputActions::Asset a;
			const InputActions::Map* map = nullptr;
			const InputActions::Action* act = a.Load(asset) ? a.FindAction(id, &map) : nullptr;
			const std::string stem = wstring_to_string(std::filesystem::path(string_to_wstring(asset)).stem().wstring());
			text = act && map ? stem + "/" + map->Name + "/" + act->Name : "Missing (Input Action Reference)";
		}
		ImVec2 fmin, fmax;
		const int pressed = UnityGUI::ObjectFieldButtons(label, text.c_str(), "input_actions", nullptr, 0, &fmin, &fmax, indent);
		const ImVec2 after = ImGui::GetCursorScreenPos();
		if (pressed == -1)
			ImGui::OpenPopup("##actionref");
		ImGui::SetNextWindowSizeConstraints(ImVec2(260, 0), ImVec2(520, 420));
		if (ImGui::BeginPopup("##actionref"))
		{
			if (ImGui::Selectable("None"))
			{
				v = nullptr;
				changed = true;
			}
			for (const std::string& path : InputActions::FindAll())
			{
				InputActions::Asset a;
				if (!a.Load(path))
					continue;
				const std::string stem = wstring_to_string(std::filesystem::path(string_to_wstring(path)).stem().wstring());
				ImGui::TextDisabled("%s", stem.c_str());
				for (const auto& m : a.Maps)
					for (const auto& act : m.Actions)
					{
						const std::string item = "  " + m.Name + "/" + act.Name + "##" + path + act.Id;
						if (ImGui::Selectable(item.c_str(), asset == path && id == act.Id))
						{
							v = { { "asset", path }, { "action", act.Id } };
							changed = true;
						}
					}
			}
			ImGui::EndPopup();
		}
		ImGui::SetCursorScreenPos(fmin);
		ImGui::InvisibleButton("##refdrop", ImVec2((std::max)(1.0f, fmax.x - fmin.x - 22.0f), fmax.y - fmin.y));
		if (ImGui::BeginDragDropTarget())
		{
			if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload("ASSET_FILE"))
			{
				const std::string dropped(static_cast<const char*>(payload->Data));
				InputActions::Asset a;
				if (InputActions::IsInputActionsPath(dropped) && a.Load(dropped) && !a.Maps.empty() && !a.Maps[0].Actions.empty())
				{
					v = { { "asset", dropped }, { "action", a.Maps[0].Actions[0].Id } };
					changed = true;
				}
			}
			ImGui::EndDragDropTarget();
		}
		ImGui::SetCursorScreenPos(after);
		return changed;
	}

	// InputAction (스크립트 안에 둔 동작): 이름 · 형식 · 바인딩 목록 (경로 글자 — 합성은 Input Actions 에셋에서)
	bool InputActionField(const char* label, json& v, int indent)
	{
		if (!v.is_object())
			v = { { "name", label }, { "type", "Button" }, { "id", InputActions::NewGuid() }, { "expectedControlType", "Button" }, { "processors", "" }, { "interactions", "" }, { "initialStateCheck", false }, { "bindings", json::array() } };
		bool changed = false;
		json& binds = v["bindings"];
		if (!binds.is_array())
			binds = json::array();
		std::string summary;
		for (const json& b : binds)
			if (!b.value("isPartOfComposite", false))
				summary += (summary.empty() ? "" : ", ") + InputActions::DisplayPath(b.value("path", std::string()), false);
		const std::string title = std::string(label) + (summary.empty() ? "" : "  (" + summary + ")");
		if (!UnityGUI::FoldoutPlain(title.c_str(), indent, false))
			return false;
		static const char* kTypes[] = { "Value", "Button", "Pass Through" };
		const std::string type = v.value("type", std::string("Button"));
		int t = type == "Value" ? 0 : (type == "PassThrough" ? 2 : 1);
		if (UnityGUI::Dropdown("Action Type", &t, kTypes, 3, indent + 1))
		{
			v["type"] = t == 0 ? "Value" : (t == 2 ? "PassThrough" : "Button");
			if (t == 1) v["expectedControlType"] = "Button";
			changed = true;
		}
		if (t != 1)
		{
			std::string control = v.value("expectedControlType", std::string());
			if (UnityGUI::TextField("Control Type", &control, indent + 1)) { v["expectedControlType"] = control; changed = true; }
		}
		int remove = -1;
		for (size_t i = 0; i < binds.size(); ++i)
		{
			ImGui::PushID((int)i);
			json& b = binds[i];
			std::string path = b.value("path", std::string());
			const std::string rowLabel = b.value("isPartOfComposite", false) ? "  " + b.value("name", std::string()) : (b.value("isComposite", false) ? "Composite" : "Binding " + std::to_string(i));
			if (UnityGUI::TextField(rowLabel.c_str(), &path, indent + 1)) { b["path"] = path; changed = true; }
			if (ImGui::IsItemHovered())
				ImGui::SetTooltip("%s", InputActions::DisplayPath(path).c_str());
			ImGui::SameLine();
			if (ImGui::SmallButton("-")) remove = (int)i;
			ImGui::PopID();
		}
		if (remove >= 0)
		{
			// 합성이면 부분도
			const bool composite = binds[(size_t)remove].value("isComposite", false);
			binds.erase((size_t)remove);
			while (composite && (size_t)remove < binds.size() && binds[(size_t)remove].value("isPartOfComposite", false))
				binds.erase((size_t)remove);
			changed = true;
		}
		bool plus = false, minus = false;
		UnityGUI::PlusMinus(&plus, &minus);
		if (plus)
		{
			binds.push_back({ { "name", "" }, { "id", InputActions::NewGuid() }, { "path", "" }, { "interactions", "" }, { "processors", "" }, { "groups", "" }, { "isComposite", false }, { "isPartOfComposite", false } });
			changed = true;
		}
		if (minus && !binds.empty()) { binds.erase(binds.size() - 1); changed = true; }
		std::string inter = v.value("interactions", std::string()), proc = v.value("processors", std::string());
		if (UnityGUI::TextField("Interactions", &inter, indent + 1)) { v["interactions"] = inter; changed = true; }
		if (UnityGUI::TextField("Processors", &proc, indent + 1)) { v["processors"] = proc; changed = true; }
		return changed;
	}

	bool ArrayField(const ScriptEngine::FieldInfo& f, json& v, const char* label, const std::string& key, int indent)
	{
		if (!v.is_array())
			v = json::array();
		bool changed = false;
		int count = (int)v.size();
		const bool open = UnityGUI::FoldoutPlain(label, indent, false);
		// 크기 칸 (Unity 의 배열 머리 오른쪽 숫자)
		{
			const ImVec2 back = ImGui::GetCursorScreenPos();
			const float w = ImGui::GetContentRegionAvail().x;
			const float rowH = UnityGUI::kRowStep;   // FoldoutPlain 한 줄
			ImGui::SetCursorScreenPos(ImVec2(back.x + w - 12.0f - 62.0f, back.y - rowH + 1.0f));
			ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(4.0f, (rowH - 2.0f - ImGui::GetFontSize()) * 0.5f));
			ImGui::SetNextItemWidth(62.0f);
			int n = count;
			if (ImGui::InputInt("##size", &n, 0, 0) && n >= 0 && n <= 100000 && n != count)
			{
				count = n;
				changed = true;
			}
			ImGui::PopStyleVar();
			ImGui::SetCursorScreenPos(back);
		}
		if (changed)
		{
			const json fill = f.Element ? f.Element->Default : json();
			// Unity: 늘리면 마지막 원소를 복사 (없으면 기본값)
			while ((int)v.size() < count)
				v.push_back(v.empty() ? fill : v.back());
			while ((int)v.size() > count)
				v.erase(v.size() - 1);
		}
		if (open && f.Element)
		{
			if (v.empty())
				UnityGUI::Label("List is Empty", indent + 1);
			for (size_t i = 0; i < v.size(); ++i)
			{
				ImGui::PushID((int)i);
				const std::string el = "Element " + std::to_string(i);
				changed |= Value(*f.Element, v[i], el.c_str(), key + "[" + std::to_string(i) + "]", indent + 1);
				ImGui::PopID();
			}
			bool plus = false, minus = false;
			UnityGUI::PlusMinus(&plus, &minus);
			if (plus) { v.push_back(v.empty() ? f.Element->Default : v.back()); changed = true; }
			if (minus && !v.empty()) { v.erase(v.size() - 1); changed = true; }
		}
		return changed;
	}

	bool StructField(const ScriptEngine::FieldInfo& f, json& v, const char* label, const std::string& key, int indent)
	{
		if (!v.is_object())
			v = f.Default.is_object() ? f.Default : json::object();
		if (!UnityGUI::FoldoutPlain(label, indent, false))
			return false;
		return !ScriptFieldGUI::Fields(f.Children, v, key + ".", indent + 1).empty();
	}

	bool Value(const ScriptEngine::FieldInfo& f, json& v, const char* label, const std::string& key, int indent)
	{
		bool changed = false;
		if (f.Type == "float")
		{
			float x = v.is_number() ? v.get<float>() : 0.0f;
			changed = f.HasRange ? UnityGUI::Slider(label, &x, f.RangeMin, f.RangeMax, indent) : UnityGUI::Float(label, &x, indent);
			if (f.HasRange) x = std::clamp(x, f.RangeMin, f.RangeMax);
			if (changed) v = x;
		}
		else if (f.Type == "int")
		{
			int x = v.is_number() ? v.get<int>() : 0;
			if (f.HasRange)
			{
				float fx = (float)x;
				changed = UnityGUI::Slider(label, &fx, f.RangeMin, f.RangeMax, indent);
				x = (int)std::lround(std::clamp(fx, f.RangeMin, f.RangeMax));
			}
			else
				changed = UnityGUI::Int(label, &x, indent);
			if (changed) v = x;
		}
		else if (f.Type == "bool")
		{
			bool x = v.is_boolean() && v.get<bool>();
			if ((changed = UnityGUI::Toggle(label, &x, indent))) v = x;
		}
		else if (f.Type == "string")
		{
			std::string x = v.is_string() ? v.get<std::string>() : std::string();
			if ((changed = UnityGUI::TextField(label, &x, indent))) v = x;
		}
		else if (f.Type == "Vector2" || f.Type == "Vector3" || f.Type == "Vector4" || f.Type == "Color")
		{
			float xyzw[4] = { 0, 0, 0, f.Type == "Color" ? 1.0f : 0.0f };
			if (v.is_array())
				for (size_t i = 0; i < v.size() && i < 4; ++i) xyzw[i] = v[i].get<float>();
			if (f.Type == "Vector2")
				changed = UnityGUI::Vector2Pair(label, "X", &xyzw[0], "Y", &xyzw[1], indent);
			else if (f.Type == "Vector3")
				changed = UnityGUI::Vector3(label, xyzw, false, indent);
			else if (f.Type == "Vector4")
			{
				changed = UnityGUI::Vector2Pair(label, "X", &xyzw[0], "Y", &xyzw[1], indent);
				changed |= UnityGUI::Vector2Pair("", "Z", &xyzw[2], "W", &xyzw[3], indent);
			}
			else
				changed = UnityGUI::Color(label, xyzw, indent);
			if (changed)
			{
				const int n = f.Type == "Vector2" ? 2 : (f.Type == "Vector3" ? 3 : 4);
				v = json::array();
				for (int i = 0; i < n; ++i) v.push_back(xyzw[i]);
			}
		}
		else if (f.Type == "enum")
		{
			const long long cur = v.is_number() ? v.get<long long>() : 0;
			int index = 0;
			for (size_t i = 0; i < f.OptionValues.size(); ++i)
				if (f.OptionValues[i] == cur) index = (int)i;
			std::vector<const char*> items;
			for (const std::string& s : f.Options) items.push_back(s.c_str());
			if (!items.empty() && (changed = UnityGUI::Dropdown(label, &index, items.data(), (int)items.size(), indent)))
				v = f.OptionValues[index];
		}
		else if (f.Type == "GameObject" || f.Type == "Transform")
		{
			// 오브젝트 참조: Hierarchy 에서 끌어 놓기 (값 = fileID)
			const uint64 id = v.is_number_unsigned() || v.is_number_integer() ? v.get<uint64>() : 0;
			const bool isGo = f.Type == "GameObject";
			const std::string text = id ? ObjectName(id, "") + (isGo ? "" : " (Transform)") : (isGo ? "None (Game Object)" : "None (Transform)");
			ImVec2 fmin, fmax;
			const int pressed = UnityGUI::ObjectFieldButtons(label, text.c_str(), isGo ? "gameobject" : "transform", nullptr, 0, &fmin, &fmax, indent);
			const ImVec2 after = ImGui::GetCursorScreenPos();
			ImGui::SetCursorScreenPos(fmin);
			ImGui::InvisibleButton("##ref", ImVec2((std::max)(1.0f, fmax.x - fmin.x - 22.0f), fmax.y - fmin.y));
			if (ImGui::BeginDragDropTarget())
			{
				if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload("GAME_OBJECT"))
				{
					GameObject* go = *(GameObject**)payload->Data;
					if (go) { v = go->GetFileID(); changed = true; }
				}
				ImGui::EndDragDropTarget();
			}
			if (ImGui::IsItemClicked() && id)
				if (Scene* scene = SceneManager::GetI()->GetCurrentScene())
					if (GameObject* target = scene->FindByFileID(id))
						SelectionManager::SetSelectedGameObject(target);
			if (pressed == -1 && id)
			{
				v = 0;   // ⊙ = 비우기 (씬 오브젝트 선택 창은 아직 없음)
				changed = true;
			}
			ImGui::SetCursorScreenPos(after);
		}
		else if (f.Type == "AudioClip")
		{
			changed = AssetRefField(label, v, key, "audio_clip", "Audio Clip",
				[]() { return AudioClip::FindAll(); },
				[](const std::string& p) { return AudioClip::IsAudioPath(p); },
				[](const std::string& p) { AudioManager::PlayPreview(AudioClip::Load(p)); }, indent);
		}
		else if (f.Type == "InputActionAsset")
		{
			changed = AssetRefField(label, v, key, "input_actions", "InputActionAsset",
				[]() { return InputActions::FindAll(); },
				[](const std::string& p) { return InputActions::IsInputActionsPath(p); },
				nullptr, indent);
		}
		else if (f.Type == "InputActionReference")
			changed = ActionReferenceField(label, v, indent);
		else if (f.Type == "InputAction")
			changed = InputActionField(label, v, indent);
		else if (f.Type == "ScriptableObject")
		{
			const std::string base = f.TypeFullName.empty() ? f.TypeName : f.TypeFullName;
			changed = AssetRefField(label, v, key, "scriptable_object", f.TypeName,
				[base]() { return ScriptableAssets::FindAll(base); },
				[base](const std::string& p) {
					std::string cls;
					json fields;
					return ScriptableAssets::Read(p, cls, fields) && ScriptEngine::IsScriptableSubclass(cls, base);
				},
				nullptr, indent);
		}
		else if (f.Type == "array")
			changed = ArrayField(f, v, label, key, indent);
		else if (f.Type == "struct")
			changed = StructField(f, v, label, key, indent);
		else
			UnityGUI::ValueLabel(label, (f.TypeName + " (not supported in Inspector yet)").c_str(), indent);
		return changed;
	}
}

namespace ScriptFieldGUI
{
	bool Field(const ScriptEngine::FieldInfo& f, json& value, const std::string& key, int indent)
	{
		if (!f.Header.empty())
		{
			UnityGUI::Spacing(6.0f);
			UnityGUI::Label(f.Header.c_str(), indent, true);
		}
		if (f.Space > 0.0f)
			UnityGUI::Spacing(f.Space);
		ImGui::PushID(f.Name.c_str());
		const bool changed = Value(f, value, f.Label.c_str(), key + f.Name, indent);
		if (!f.Tooltip.empty() && ImGui::IsItemHovered(ImGuiHoveredFlags_DelayNormal))
			ImGui::SetTooltip("%s", f.Tooltip.c_str());
		ImGui::PopID();
		return changed;
	}

	std::vector<std::string> Fields(const std::vector<ScriptEngine::FieldInfo>& fields, json& values, const std::string& key, int indent)
	{
		std::vector<std::string> changed;
		if (!values.is_object())
			values = json::object();
		for (const ScriptEngine::FieldInfo& f : fields)
		{
			json v = values.contains(f.Name) ? values[f.Name] : f.Default;
			if (Field(f, v, key, indent))
			{
				values[f.Name] = v;
				changed.push_back(f.Name);
			}
		}
		return changed;
	}
}
