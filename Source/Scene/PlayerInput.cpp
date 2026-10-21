#include "pch.h"
#include "PlayerInput.h"
#include "InputActionsAsset.h"
#include "UnityGUI.h"
#include "ObjectPicker.h"
#include "SelectionManager.h"
#include <filesystem>
#include <map>

namespace fs = std::filesystem;
using json = nlohmann::json;

namespace
{
	std::vector<PlayerInput*>& Registry()
	{
		static std::vector<PlayerInput*> r;
		return r;
	}

	// Inspector 가 프레임마다 에셋을 다시 읽지 않게 (파일 시각이 바뀌면 다시)
	struct CachedAsset
	{
		fs::file_time_type Time{};
		bool Ok = false;
		InputActions::Asset Asset;
	};

	const InputActions::Asset* LoadCached(const std::string& path)
	{
		static std::map<std::string, CachedAsset> s_Cache;
		if (path.empty())
			return nullptr;
		std::error_code ec;
		const fs::file_time_type t = fs::last_write_time(fs::path(InputActions::FullPath(path)), ec);
		if (ec)
			return nullptr;
		CachedAsset& c = s_Cache[path];
		if (c.Time != t)
		{
			c.Time = t;
			c.Ok = c.Asset.Load(path);
		}
		return c.Ok ? &c.Asset : nullptr;
	}

	std::string Stem(const std::string& path)
	{
		return wstring_to_string(fs::path(string_to_wstring(path)).stem().wstring());
	}

	std::string MessageName(const std::string& action)
	{
		std::string id;
		for (char c : action)
			if (std::isalnum((unsigned char)c) || c == '_') id += c;
		if (!id.empty()) id[0] = (char)std::toupper((unsigned char)id[0]);
		return "On" + id;
	}

	std::string UniqueAssetPath(const std::string& base)
	{
		std::string path = "Assets\\" + base + ".inputactions";
		std::error_code ec;
		for (int n = 1; fs::exists(fs::path(InputActions::FullPath(path)), ec); ++n)
			path = "Assets\\" + base + " " + std::to_string(n) + ".inputactions";
		return path;
	}
}

PlayerInput::PlayerInput()
{
	m_InspectorTitleName = "Player Input";
	Registry().push_back(this);
}

PlayerInput::~PlayerInput()
{
	auto& r = Registry();
	r.erase(std::remove(r.begin(), r.end(), this), r.end());
}

const std::vector<PlayerInput*>& PlayerInput::All() { return Registry(); }

bool PlayerInput::IsActiveInHierarchy() const
{
	for (GameObject* g = m_pGameObject; g != nullptr; g = g->GetParent())
		if (!g->IsActive())
			return false;
	return m_pGameObject != nullptr;
}

json PlayerInput::RuntimeSettings() const
{
	json events = json::array();
	for (const ActionEvent& e : m_Events)
		if (!e.Calls.Calls.empty())
			events.push_back({ { "actionId", e.ActionId }, { "actionName", e.ActionName }, { "calls", e.Calls.ToJson() } });
	return {
		{ "actions", m_Actions }, { "defaultScheme", m_DefaultScheme }, { "defaultMap", m_DefaultMap },
		{ "behavior", (int)m_Behavior }, { "neverAutoSwitch", m_NeverAutoSwitch }, { "events", events },
		{ "deviceLost", m_DeviceLost.ToJson() }, { "deviceRegained", m_DeviceRegained.ToJson() }, { "controlsChanged", m_ControlsChanged.ToJson() },
	};
}

// 에셋의 동작마다 이벤트 칸 (Unity 의 m_ActionEvents — 이미 넣은 호출은 id 로 이어간다)
void PlayerInput::SyncEvents(const InputActions::Asset& asset)
{
	std::vector<ActionEvent> next;
	for (const InputActions::Map& m : asset.Maps)
		for (const InputActions::Action& a : m.Actions)
		{
			ActionEvent e;
			e.ActionId = a.Id;
			e.ActionName = m.Name + "/" + a.Name;
			for (const ActionEvent& old : m_Events)
				if (old.ActionId == a.Id) { e.Calls = old.Calls; break; }
			next.push_back(std::move(e));
		}
	m_Events = std::move(next);
}

void PlayerInput::OnInspectorGUI()
{
	const InputActions::Asset* asset = LoadCached(m_Actions);

	// Actions: ⊙ = 고르기, Project 에서 끌어 놓기, 두 번 클릭 = Project 에서 고르기
	{
		std::string text = m_Actions.empty() ? std::string("None (Input Action Asset)") : Stem(m_Actions) + (asset ? " (Input Action Asset)" : " (Missing)");
		ImVec2 fmin, fmax;
		const int pressed = UnityGUI::ObjectFieldButtons("Actions", text.c_str(), "input_actions", nullptr, 0, &fmin, &fmax);
		const ImVec2 after = ImGui::GetCursorScreenPos();
		const std::string key = "playerInputActions:" + std::to_string((uintptr_t)this);
		if (pressed == -1)
		{
			ObjectPicker::Options opt;
			opt.TypeName = "InputActionAsset";
			opt.Icon = "input_actions";
			opt.Items = InputActions::FindAll();
			opt.Current = m_Actions;
			ObjectPicker::Open(key, std::move(opt));
		}
		std::string picked;
		auto assign = [&](const std::string& p) {
			m_Actions = p;
			if (const InputActions::Asset* a = LoadCached(p))
			{
				if (a->FindMap(m_DefaultMap) < 0)
					m_DefaultMap = a->Maps.empty() ? std::string() : a->Maps[0].Name;
				if (!m_DefaultScheme.empty() && a->FindScheme(m_DefaultScheme) < 0)
					m_DefaultScheme.clear();
			}
		};
		if (ObjectPicker::Poll(key, picked))
			assign(picked);
		ImGui::SetCursorScreenPos(fmin);
		ImGui::InvisibleButton("##actionsDrop", ImVec2((std::max)(1.0f, fmax.x - fmin.x - 22.0f), fmax.y - fmin.y));
		if (ImGui::IsItemHovered() && ImGui::IsMouseDoubleClicked(ImGuiMouseButton_Left) && !m_Actions.empty())
			SelectionManager::SetSelectedFile(InputActions::FullPath(m_Actions));
		if (ImGui::BeginDragDropTarget())
		{
			if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload("ASSET_FILE"))
			{
				const std::string dropped(static_cast<const char*>(payload->Data));
				if (InputActions::IsInputActionsPath(dropped))
					assign(dropped);
			}
			ImGui::EndDragDropTarget();
		}
		ImGui::SetCursorScreenPos(after);
		asset = LoadCached(m_Actions);
	}

	if (m_Actions.empty())
	{
		UnityGUI::HelpBox("Assign an Input Action Asset, or create one with the default Player and UI maps (Keyboard&Mouse, Gamepad and Touch schemes).", false);
		if (UnityGUI::CenterButton("Create Actions..."))
		{
			const std::string base = m_pGameObject ? m_pGameObject->GetName() + " Controls" : std::string("Controls");
			const std::string path = UniqueAssetPath(base);
			InputActions::DefaultAsset(Stem(path)).Save(path);
			m_Actions = path;
			m_DefaultMap = "Player";
			if (OpenEditor) OpenEditor(path);
		}
		return;
	}
	if (asset == nullptr)
	{
		UnityGUI::HelpBox(("Cannot read " + m_Actions + " (not an Input Actions JSON?)").c_str(), true);
		return;
	}

	// Default Scheme · Auto-Switch
	{
		std::vector<std::string> names = { "<Any>" };
		for (const auto& s : asset->Schemes) names.push_back(s.Name);
		std::vector<const char*> items;
		for (const std::string& n : names) items.push_back(n.c_str());
		int index = 0;
		for (size_t i = 1; i < names.size(); ++i)
			if (names[i] == m_DefaultScheme) index = (int)i;
		if (UnityGUI::Dropdown("Default Scheme", &index, items.data(), (int)items.size()))
			m_DefaultScheme = index == 0 ? std::string() : names[(size_t)index];
		if (asset->Schemes.size() > 1)
		{
			bool autoSwitch = !m_NeverAutoSwitch;
			if (UnityGUI::Toggle("Auto-Switch", &autoSwitch))
				m_NeverAutoSwitch = !autoSwitch;
		}
	}
	// Default Map
	{
		std::vector<std::string> names = { "<None>" };
		for (const auto& m : asset->Maps) names.push_back(m.Name);
		std::vector<const char*> items;
		for (const std::string& n : names) items.push_back(n.c_str());
		int index = 0;
		for (size_t i = 1; i < names.size(); ++i)
			if (names[i] == m_DefaultMap) index = (int)i;
		if (UnityGUI::Dropdown("Default Map", &index, items.data(), (int)items.size()))
			m_DefaultMap = index == 0 ? std::string() : names[(size_t)index];
	}
	// Behavior
	{
		static const char* kBehaviors[] = { "Send Messages", "Broadcast Messages", "Invoke Unity Events", "Invoke C Sharp Events" };
		int b = (int)m_Behavior;
		if (UnityGUI::Dropdown("Behavior", &b, kBehaviors, 4))
			m_Behavior = (Behavior)b;
	}

	if (m_Behavior == Behavior::SendMessages || m_Behavior == Behavior::BroadcastMessages)
	{
		std::string text = m_Behavior == Behavior::SendMessages ? "Will SendMessage() to GameObject: " : "Will BroadcastMessage() to GameObject: ";
		text += "OnDeviceLost, OnDeviceRegained, OnControlsChanged";
		for (const auto& m : asset->Maps)
			for (const auto& a : m.Actions)
				text += ", " + MessageName(a.Name);
		UnityGUI::HelpBox(text.c_str(), false);
	}
	else if (m_Behavior == Behavior::InvokeCSharpEvents)
		UnityGUI::HelpBox("Will invoke the C# events onActionTriggered (all phases), onDeviceLost, onDeviceRegained and onControlsChanged.", false);
	else
	{
		SyncEvents(*asset);
		if (UnityGUI::Foldout("Events", 0, false))
		{
			for (const auto& m : asset->Maps)
			{
				ImGui::PushID(m.Name.c_str());
				if (UnityGUI::FoldoutPlain(m.Name.c_str(), 1, false))
					for (ActionEvent& e : m_Events)
					{
						if (e.ActionName.compare(0, m.Name.size() + 1, m.Name + "/") != 0)
							continue;
						ImGui::PushID(e.ActionId.c_str());
						const std::string title = e.ActionName.substr(m.Name.size() + 1) + " (CallbackContext)";
						e.Calls.Draw(title.c_str(), "CallbackContext");
						ImGui::PopID();
					}
				ImGui::PopID();
			}
			m_DeviceLost.Draw("Device Lost Event (PlayerInput)", "PlayerInput");
			m_DeviceRegained.Draw("Device Regained Event (PlayerInput)", "PlayerInput");
			m_ControlsChanged.Draw("Controls Changed Event (PlayerInput)", "PlayerInput");
		}
	}

	if (OpenEditor && UnityGUI::CenterButton("Edit Asset"))
		OpenEditor(m_Actions);
}

GENERATE_COMPONENT_FUNC_TOJSON(PlayerInput)
{
	json j;
	SERIALIZE_TYPE(j, PlayerInput);
	j["enabled"] = m_Enabled;
	j["actions"] = m_Actions;
	j["defaultScheme"] = m_DefaultScheme;
	j["defaultActionMap"] = m_DefaultMap;
	static const char* kBehaviors[] = { "SendMessages", "BroadcastMessages", "InvokeUnityEvents", "InvokeCSharpEvents" };
	j["notificationBehavior"] = kBehaviors[std::clamp((int)m_Behavior, 0, 3)];
	j["neverAutoSwitchControlSchemes"] = m_NeverAutoSwitch;
	json events = json::array();
	for (const ActionEvent& e : m_Events)
		if (!e.Calls.Calls.empty())
			events.push_back({ { "actionId", e.ActionId }, { "actionName", e.ActionName }, { "calls", e.Calls.ToJson() } });
	j["actionEvents"] = events;
	j["deviceLostEvent"] = m_DeviceLost.ToJson();
	j["deviceRegainedEvent"] = m_DeviceRegained.ToJson();
	j["controlsChangedEvent"] = m_ControlsChanged.ToJson();
	return j;
}

GENERATE_COMPONENT_FUNC_FROMJSON(PlayerInput)
{
	m_Enabled = j.value("enabled", true);
	m_Actions = j.value("actions", std::string());
	m_DefaultScheme = j.value("defaultScheme", std::string());
	m_DefaultMap = j.value("defaultActionMap", std::string("Player"));
	m_Behavior = Behavior::SendMessages;
	if (j.contains("notificationBehavior"))
	{
		const json& b = j["notificationBehavior"];
		if (b.is_number_integer()) m_Behavior = (Behavior)std::clamp(b.get<int>(), 0, 3);
		else if (b.is_string())
		{
			const std::string s = b.get<std::string>();
			if (s == "BroadcastMessages") m_Behavior = Behavior::BroadcastMessages;
			else if (s == "InvokeUnityEvents") m_Behavior = Behavior::InvokeUnityEvents;
			else if (s == "InvokeCSharpEvents") m_Behavior = Behavior::InvokeCSharpEvents;
		}
	}
	m_NeverAutoSwitch = j.value("neverAutoSwitchControlSchemes", false);
	m_Events.clear();
	if (j.contains("actionEvents") && j["actionEvents"].is_array())
		for (const json& e : j["actionEvents"])
		{
			ActionEvent ev;
			ev.ActionId = e.value("actionId", std::string());
			ev.ActionName = e.value("actionName", std::string());
			if (e.contains("calls")) ev.Calls.FromJson(e["calls"]);
			m_Events.push_back(std::move(ev));
		}
	if (j.contains("deviceLostEvent")) m_DeviceLost.FromJson(j["deviceLostEvent"]);
	if (j.contains("deviceRegainedEvent")) m_DeviceRegained.FromJson(j["deviceRegainedEvent"]);
	if (j.contains("controlsChangedEvent")) m_ControlsChanged.FromJson(j["controlsChangedEvent"]);
}

// ---------------------------------------------------------------- C# (NovaEngine.InputSystem.InputNative)
// 켜진 PlayerInput (컴포넌트 · GameObject 가 켜져 있고 씬에 있는 것) 의 GameObject id
NOVA_PACKAGE_EXPORT int NovaPlayerInput_List(uint64* ids, int max)
{
	int n = 0;
	for (PlayerInput* p : PlayerInput::All())
	{
		GameObject* go = p->GetGameObject();
		if (go == nullptr || !p->IsEnabled() || !p->IsActiveInHierarchy())
			continue;
		if (ids && n < max)
			ids[n] = go->GetFileID();
		++n;
	}
	return (std::min)(n, max);
}

NOVA_PACKAGE_EXPORT const char* NovaPlayerInput_Settings(uint64 id)
{
	thread_local std::string out;
	out = "{}";
	for (PlayerInput* p : PlayerInput::All())
		if (GameObject* go = p->GetGameObject(); go && go->GetFileID() == id)
		{
			out = p->RuntimeSettings().dump();
			break;
		}
	return out.c_str();
}
