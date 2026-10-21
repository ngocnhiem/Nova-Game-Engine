#include "pch.h"
#include "InputActionsEditorWindow.h"
#include "InputDevices.h"
#include "Debug.h"
#include "PlayerInput.h"
#include "UnityGUI.h"
#include "EditorExtensions.h"
#include "CliServer.h"
#include "ScriptEngine.h"
#include "SelectionManager.h"
#include "ObjectPicker.h"
#include <filesystem>
#include <sstream>

namespace fs = std::filesystem;
using json = nlohmann::json;
using namespace InputActions;

InputActionsEditorWindow* InputActionsEditorWindow::s_Instance = nullptr;

namespace
{
	const ImU32 kHeader = IM_COL32(53, 53, 53, 255);
	const ImU32 kPanel = IM_COL32(40, 40, 40, 255);
	const ImU32 kBorder = IM_COL32(25, 25, 25, 255);
	const ImU32 kText = IM_COL32(210, 210, 210, 255);
	const ImU32 kDim = IM_COL32(140, 140, 140, 255);

	std::string Lower(std::string s) { for (char& c : s) c = (char)std::tolower((unsigned char)c); return s; }
	bool Contains(const std::string& hay, const std::string& needle) { return needle.empty() || Lower(hay).find(Lower(needle)) != std::string::npos; }

	bool HasGroup(const std::string& groups, const std::string& g)
	{
		std::stringstream ss(groups);
		std::string part;
		while (std::getline(ss, part, ';'))
			if (!part.empty() && Lower(part) == Lower(g)) return true;
		return false;
	}

	std::string SetGroup(const std::string& groups, const std::string& g, bool on)
	{
		std::vector<std::string> parts;
		std::stringstream ss(groups);
		std::string part;
		while (std::getline(ss, part, ';'))
			if (!part.empty() && Lower(part) != Lower(g)) parts.push_back(part);
		if (on) parts.push_back(g);
		std::string out;
		for (const std::string& p : parts) out += (out.empty() ? "" : ";") + p;
		return out;
	}

	void SectionHeader(const char* text, float width, float y)
	{
		ImDrawList* dl = ImGui::GetWindowDrawList();
		const ImVec2 p = ImGui::GetCursorScreenPos();
		dl->AddRectFilled(p, ImVec2(p.x + width, p.y + y), kHeader);
		dl->AddText(UnityGUI::BoldFont(), ImGui::GetFontSize(), ImVec2(p.x + 8, p.y + (y - ImGui::GetFontSize()) * 0.5f), kText, text);
		ImGui::Dummy(ImVec2(width, y));
	}

	bool HeaderPlus(const char* id, float right, float y, float h)
	{
		const ImVec2 back = ImGui::GetCursorScreenPos();
		ImGui::SetCursorScreenPos(ImVec2(right - 24, y + (h - 18) * 0.5f));
		ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(4, 1));
		const bool pressed = ImGui::Button((std::string("+##") + id).c_str(), ImVec2(20, 18));
		ImGui::PopStyleVar();
		ImGui::SetCursorScreenPos(back);
		return pressed;
	}

	std::string UniqueName(const std::vector<std::string>& taken, const std::string& base)
	{
		auto used = [&](const std::string& n) { for (const auto& t : taken) if (Lower(t) == Lower(n)) return true; return false; };
		if (!used(base)) return base;
		for (int i = 1; ; ++i)
		{
			const std::string n = base + std::to_string(i);
			if (!used(n)) return n;
		}
	}

	std::vector<std::string> MapNames(const Asset& a) { std::vector<std::string> v; for (const auto& m : a.Maps) v.push_back(m.Name); return v; }
	std::vector<std::string> ActionNames(const Map& m) { std::vector<std::string> v; for (const auto& a : m.Actions) v.push_back(a.Name); return v; }

	Binding MakeBinding(const std::string& action, const std::string& path, const std::string& name = "", bool composite = false, bool part = false)
	{
		Binding b;
		b.Name = name;
		b.Id = NewGuid();
		b.Path = path;
		b.Action = action;
		b.IsComposite = composite;
		b.IsPartOfComposite = part;
		return b;
	}

	// "2DVector(mode=2)" 의 매개변수 하나
	std::string CompositeParam(const std::string& path, const std::string& key, const std::string& def)
	{
		for (const NameAndParams& n : ParseList(path))
			for (const auto& p : n.Params)
				if (Lower(p.first) == Lower(key)) return p.second;
		return def;
	}

	std::string SetCompositeParam(const std::string& path, const std::string& key, const std::string& value, const std::string& def)
	{
		std::vector<NameAndParams> list = ParseList(path);
		if (list.empty()) return path;
		auto& ps = list[0].Params;
		ps.erase(std::remove_if(ps.begin(), ps.end(), [&](const auto& p) { return Lower(p.first) == Lower(key); }), ps.end());
		if (value != def) ps.push_back({ key, value });
		return JoinList({ list[0] });
	}

	const NamedInfo* FindNamed(const std::vector<NamedInfo>& list, const std::string& name)
	{
		for (const NamedInfo& n : list)
			if (Lower(n.Name) == Lower(name)) return &n;
		return nullptr;
	}

	// XInput 비트 → Gamepad 컨트롤 이름
	const std::pair<uint32, const char*> kPadBits[] = {
		{ 0x1000, "buttonSouth" }, { 0x2000, "buttonEast" }, { 0x4000, "buttonWest" }, { 0x8000, "buttonNorth" },
		{ 0x0100, "leftShoulder" }, { 0x0200, "rightShoulder" }, { 0x0010, "start" }, { 0x0020, "select" },
		{ 0x0040, "leftStickPress" }, { 0x0080, "rightStickPress" },
		{ 0x0001, "dpad/up" }, { 0x0002, "dpad/down" }, { 0x0004, "dpad/left" }, { 0x0008, "dpad/right" },
	};

	uint32 PadBit(const std::string& name)
	{
		std::string n = Lower(name);
		if (n == "a" || n == "cross") n = "buttonsouth";
		if (n == "b" || n == "circle") n = "buttoneast";
		if (n == "x" || n == "square") n = "buttonwest";
		if (n == "y" || n == "triangle") n = "buttonnorth";
		for (const auto& kb : kPadBits)
			if (Lower(kb.second) == n) return kb.first;
		return 0;
	}

	int MouseVk(const std::string& name)
	{
		const std::string n = Lower(name);
		if (n == "left" || n == "leftbutton") return VK_LBUTTON;
		if (n == "right" || n == "rightbutton") return VK_RBUTTON;
		if (n == "middle" || n == "middlebutton") return VK_MBUTTON;
		if (n == "back" || n == "backbutton") return VK_XBUTTON1;
		if (n == "forward" || n == "forwardbutton") return VK_XBUTTON2;
		return 0;
	}

	bool Vec2Arg(const json& a, const char* key, float& x, float& y)
	{
		if (!a.contains(key)) return false;
		const json& v = a[key];
		if (v.is_array() && v.size() >= 2) { x = v[0].get<float>(); y = v[1].get<float>(); return true; }
		if (v.is_string())
		{
			const std::string s = v.get<std::string>();
			if (sscanf_s(s.c_str(), "%f,%f", &x, &y) == 2) return true;
		}
		return false;
	}

	// ---------------------------------------------------------------- nova inputactions
	bool InputActionsCli(const json& a, json& r, std::string& e)
	{
		const std::string op = a.value("op", std::string("help"));
		std::string path = a.value("path", std::string());
		std::replace(path.begin(), path.end(), '/', '\\');
		if (op == "help")
		{
			r = { { "ops", json::array({
				"list", "create <Assets/X.inputactions> [--empty]", "info <path>", "window <path>", "close",
				"add-map <path> --name Player", "add-action <path> --map Player --name Fire [--type Button|Value|PassThrough] [--control Vector2] [--interactions Hold] [--processors ...] [--initial true]",
				"add-binding <path> --action Player/Fire --binding \"<Mouse>/leftButton\" [--groups Keyboard&Mouse] [--interactions ...] [--processors ...]",
				"add-composite <path> --action Player/Move --composite 2DVector [--name WASD] --parts \"up=<Keyboard>/w;down=<Keyboard>/s;left=<Keyboard>/a;right=<Keyboard>/d\" [--groups ...]",
				"set <path> --action Player/Fire [--rename X] [--type ...] [--control ...] [--interactions ...] [--processors ...] [--initial true]",
				"set-binding <path> --action Player/Fire --index 0 [--binding ...] [--groups ...] [--interactions ...] [--processors ...]",
				"remove <path> --map M | --action M/A | --action M/A --index i",
				"add-scheme <path> --name Gamepad --devices \"<Gamepad>\" [--optional \"<Mouse>\"]", "remove-scheme <path> --name X",
				"generate <path> [--class Name] [--namespace NS] [--file Assets/X.cs]",
				"project-wide [--set <path> | --create | --clear]", "settings [--defaultHoldTime 0.4 ...]" }) } };
			return true;
		}
		if (op == "list")
		{
			r = { { "assets", FindAll() }, { "projectWide", ProjectSettings().ProjectWideActions } };
			return true;
		}
		if (op == "close")
		{
			if (InputActionsEditorWindow::IsOpen()) InputActionsEditorWindow::Open(std::string());
			r = { { "open", false } };
			return true;
		}
		if (op == "project-wide")
		{
			Settings& s = ProjectSettings();
			if (a.value("create", false))
			{
				const std::string p = "Assets\\InputSystem_Actions.inputactions";
				std::error_code ec;
				if (!fs::exists(fs::path(FullPath(p)), ec))
					DefaultAsset("InputSystem_Actions").Save(p);
				s.ProjectWideActions = p;
				SaveProjectSettings();
			}
			else if (a.contains("set"))
			{
				std::string p = a["set"].is_string() ? a["set"].get<std::string>() : std::string();
				std::replace(p.begin(), p.end(), '/', '\\');
				s.ProjectWideActions = p;
				SaveProjectSettings();
			}
			else if (a.value("clear", false))
			{
				s.ProjectWideActions.clear();
				SaveProjectSettings();
			}
			r = { { "projectWideActions", s.ProjectWideActions } };
			return true;
		}
		if (op == "settings")
		{
			Settings& s = ProjectSettings();
			auto f = [&](const char* k, float& v) { if (a.contains(k) && a[k].is_number()) v = a[k].get<float>(); };
			f("defaultDeadzoneMin", s.DefaultDeadzoneMin); f("defaultDeadzoneMax", s.DefaultDeadzoneMax);
			f("defaultButtonPressPoint", s.DefaultButtonPressPoint); f("buttonReleaseThreshold", s.ButtonReleaseThreshold);
			f("defaultTapTime", s.DefaultTapTime); f("defaultSlowTapTime", s.DefaultSlowTapTime); f("defaultHoldTime", s.DefaultHoldTime);
			f("tapRadius", s.TapRadius); f("multiTapDelayTime", s.MultiTapDelayTime);
			if (a.contains("updateMode") && a["updateMode"].is_number()) s.UpdateMode = std::clamp(a["updateMode"].get<int>(), 1, 3);
			SaveProjectSettings();
			r = { { "defaultDeadzoneMin", s.DefaultDeadzoneMin }, { "defaultDeadzoneMax", s.DefaultDeadzoneMax }, { "defaultButtonPressPoint", s.DefaultButtonPressPoint },
				{ "buttonReleaseThreshold", s.ButtonReleaseThreshold }, { "defaultTapTime", s.DefaultTapTime }, { "defaultSlowTapTime", s.DefaultSlowTapTime },
				{ "defaultHoldTime", s.DefaultHoldTime }, { "tapRadius", s.TapRadius }, { "multiTapDelayTime", s.MultiTapDelayTime }, { "updateMode", s.UpdateMode } };
			return true;
		}
		if (path.empty()) { e = "need an asset path (Assets/X.inputactions)"; return false; }
		if (!IsInputActionsPath(path)) path += ".inputactions";
		if (op == "create")
		{
			Asset asset = a.value("empty", false) ? Asset() : DefaultAsset(fs::path(string_to_wstring(path)).stem().string());
			if (asset.Name.empty()) asset.Name = wstring_to_string(fs::path(string_to_wstring(path)).stem().wstring());
			if (!asset.Save(path, &e)) return false;
			r = { { "path", path }, { "maps", (int)asset.Maps.size() }, { "schemes", (int)asset.Schemes.size() } };
			return true;
		}
		if (op == "window")
		{
			InputActionsEditorWindow::Open(path);
			bool selected = false;
			if (a.contains("action"))
				selected = InputActionsEditorWindow::Select(a["action"].get<std::string>(), a.value("binding", -1));
			r = { { "open", true }, { "path", path }, { "selected", selected } };
			return true;
		}
		Asset asset;
		if (!asset.Load(path, &e)) return false;
		bool changed = false;
		auto findAction = [&](Map*& map, Action*& act) -> bool {
			const std::string name = a.value("action", std::string());
			act = asset.FindAction(name, &map);
			if (!act) { e = "no action '" + name + "' (use Map/Action)"; return false; }
			return true;
		};
		if (op == "info")
		{
			json maps = json::array();
			for (const Map& m : asset.Maps)
			{
				json acts = json::array();
				for (const Action& act : m.Actions)
				{
					json binds = json::array();
					for (int i : m.BindingsOf(act.Name))
					{
						const Binding& b = m.Bindings[(size_t)i];
						binds.push_back({ { "path", b.Path }, { "name", b.Name }, { "groups", b.Groups }, { "composite", b.IsComposite }, { "part", b.IsPartOfComposite },
							{ "display", b.IsComposite ? DisplayBinding(m, i) : DisplayPath(b.Path) }, { "interactions", b.Interactions }, { "processors", b.Processors } });
					}
					acts.push_back({ { "name", act.Name }, { "id", act.Id }, { "type", act.Type }, { "control", act.ExpectedControlType }, { "interactions", act.Interactions },
						{ "processors", act.Processors }, { "initialStateCheck", act.InitialStateCheck }, { "bindings", binds } });
				}
				maps.push_back({ { "name", m.Name }, { "id", m.Id }, { "actions", acts } });
			}
			json schemes = json::array();
			for (const ControlScheme& s : asset.Schemes)
			{
				json devs = json::array();
				for (const auto& d : s.Devices) devs.push_back(d.DevicePath + (d.IsOptional ? " (optional)" : ""));
				schemes.push_back({ { "name", s.Name }, { "bindingGroup", s.BindingGroup }, { "devices", devs } });
			}
			const ImportSettings imp = LoadImportSettings(path);
			r = { { "name", asset.Name }, { "path", path }, { "maps", maps }, { "controlSchemes", schemes }, { "generateCSharp", imp.GenerateWrapperCode } };
			return true;
		}
		if (op == "add-map")
		{
			const std::string name = a.value("name", std::string("New action map"));
			if (asset.FindMap(name) >= 0) { e = "map '" + name + "' exists"; return false; }
			asset.Maps.push_back({ name, NewGuid(), {}, {} });
			changed = true;
			r = { { "map", name } };
		}
		else if (op == "add-action")
		{
			const int mi = asset.FindMap(a.value("map", std::string()));
			if (mi < 0) { e = "no map '" + a.value("map", std::string()) + "'"; return false; }
			Map& m = asset.Maps[(size_t)mi];
			const std::string name = a.value("name", std::string("New action"));
			if (m.FindAction(name) >= 0) { e = "action '" + name + "' exists"; return false; }
			Action act;
			act.Name = name;
			act.Id = NewGuid();
			const std::string type = a.value("type", std::string("Button"));
			act.Type = Lower(type) == "value" ? "Value" : (Lower(type) == "passthrough" ? "PassThrough" : "Button");
			act.ExpectedControlType = a.value("control", std::string(act.Type == "Button" ? "Button" : ""));
			act.Interactions = a.value("interactions", std::string());
			act.Processors = a.value("processors", std::string());
			act.InitialStateCheck = a.value("initial", act.Type == "Value");
			m.Actions.push_back(act);
			changed = true;
			r = { { "action", m.Name + "/" + name }, { "id", act.Id } };
		}
		else if (op == "add-binding")
		{
			Map* m = nullptr; Action* act = nullptr;
			if (!findAction(m, act)) return false;
			Binding b = MakeBinding(act->Name, a.value("binding", std::string()));
			b.Groups = a.value("groups", std::string());
			b.Interactions = a.value("interactions", std::string());
			b.Processors = a.value("processors", std::string());
			m->Bindings.insert(m->Bindings.begin() + m->InsertPointFor(act->Name), b);
			changed = true;
			r = { { "binding", b.Path }, { "id", b.Id } };
		}
		else if (op == "add-composite")
		{
			Map* m = nullptr; Action* act = nullptr;
			if (!findAction(m, act)) return false;
			const std::string comp = a.value("composite", std::string("2DVector"));
			const CompositeInfo* info = FindComposite(comp);
			if (!info) { e = "no composite '" + comp + "' (1DAxis, 2DVector, 3DVector, ButtonWithOneModifier, ButtonWithTwoModifiers, OneModifier, TwoModifiers)"; return false; }
			std::vector<Binding> add;
			add.push_back(MakeBinding(act->Name, comp, a.value("name", std::string(info->Display)), true));
			std::string parts = a.value("parts", std::string());
			std::replace(parts.begin(), parts.end(), ',', ';');
			std::stringstream ss(parts);
			std::string item;
			const std::string groups = a.value("groups", std::string());
			while (std::getline(ss, item, ';'))
			{
				const size_t eq = item.find('=');
				if (eq == std::string::npos) continue;
				Binding p = MakeBinding(act->Name, item.substr(eq + 1), item.substr(0, eq), false, true);
				p.Groups = groups;
				add.push_back(p);
			}
			m->Bindings.insert(m->Bindings.begin() + m->InsertPointFor(act->Name), add.begin(), add.end());
			changed = true;
			r = { { "composite", comp }, { "parts", (int)add.size() - 1 } };
		}
		else if (op == "set")
		{
			Map* m = nullptr; Action* act = nullptr;
			if (!findAction(m, act)) return false;
			if (a.contains("type"))
			{
				const std::string type = Lower(a["type"].get<std::string>());
				act->Type = type == "value" ? "Value" : (type == "passthrough" ? "PassThrough" : "Button");
			}
			if (a.contains("control")) act->ExpectedControlType = a["control"].get<std::string>();
			if (a.contains("interactions")) act->Interactions = a["interactions"].is_string() ? a["interactions"].get<std::string>() : std::string();
			if (a.contains("processors")) act->Processors = a["processors"].is_string() ? a["processors"].get<std::string>() : std::string();
			if (a.contains("initial")) act->InitialStateCheck = a["initial"].get<bool>();
			if (a.contains("rename")) m->RenameAction(act->Name, a["rename"].get<std::string>());
			changed = true;
			r = { { "ok", true } };
		}
		else if (op == "set-binding")
		{
			Map* m = nullptr; Action* act = nullptr;
			if (!findAction(m, act)) return false;
			const std::vector<int> idx = m->BindingsOf(act->Name);
			const int i = a.value("index", 0);
			if (i < 0 || i >= (int)idx.size()) { e = "binding index out of range (0.." + std::to_string((int)idx.size() - 1) + ")"; return false; }
			Binding& b = m->Bindings[(size_t)idx[(size_t)i]];
			if (a.contains("binding")) b.Path = a["binding"].get<std::string>();
			if (a.contains("groups")) b.Groups = a["groups"].is_string() ? a["groups"].get<std::string>() : std::string();
			if (a.contains("interactions")) b.Interactions = a["interactions"].is_string() ? a["interactions"].get<std::string>() : std::string();
			if (a.contains("processors")) b.Processors = a["processors"].is_string() ? a["processors"].get<std::string>() : std::string();
			changed = true;
			r = { { "binding", b.Path } };
		}
		else if (op == "remove")
		{
			if (a.contains("action"))
			{
				Map* m = nullptr; Action* act = nullptr;
				if (!findAction(m, act)) return false;
				if (a.contains("index"))
				{
					const std::vector<int> idx = m->BindingsOf(act->Name);
					const int i = a["index"].get<int>();
					if (i < 0 || i >= (int)idx.size()) { e = "binding index out of range"; return false; }
					m->RemoveBinding(idx[(size_t)i]);
				}
				else
					m->RemoveAction(act->Name);
			}
			else if (a.contains("map"))
			{
				const int mi = asset.FindMap(a["map"].get<std::string>());
				if (mi < 0) { e = "no map"; return false; }
				asset.Maps.erase(asset.Maps.begin() + mi);
			}
			else { e = "remove what? --map or --action [--index]"; return false; }
			changed = true;
			r = { { "ok", true } };
		}
		else if (op == "add-scheme")
		{
			ControlScheme s;
			s.Name = a.value("name", std::string("New Control Scheme"));
			s.BindingGroup = a.value("group", s.Name);
			auto addDevices = [&](const char* key, bool optional) {
				std::string list = a.value(key, std::string());
				std::stringstream ss(list);
				std::string d;
				while (std::getline(ss, d, ','))
					if (!d.empty()) s.Devices.push_back({ d, optional, false });
			};
			addDevices("devices", false);
			addDevices("optional", true);
			const int existing = asset.FindScheme(s.Name);
			if (existing >= 0) asset.Schemes[(size_t)existing] = s;
			else asset.Schemes.push_back(s);
			changed = true;
			r = { { "scheme", s.Name }, { "devices", (int)s.Devices.size() } };
		}
		else if (op == "remove-scheme")
		{
			const int si = asset.FindScheme(a.value("name", std::string()));
			if (si < 0) { e = "no scheme"; return false; }
			asset.Schemes.erase(asset.Schemes.begin() + si);
			changed = true;
			r = { { "ok", true } };
		}
		else if (op == "generate")
		{
			ImportSettings imp = LoadImportSettings(path);
			imp.GenerateWrapperCode = true;
			if (a.contains("class")) imp.WrapperClassName = a["class"].get<std::string>();
			if (a.contains("namespace")) imp.WrapperCodeNamespace = a["namespace"].get<std::string>();
			if (a.contains("file")) { imp.WrapperCodePath = a["file"].get<std::string>(); std::replace(imp.WrapperCodePath.begin(), imp.WrapperCodePath.end(), '/', '\\'); }
			SaveImportSettings(path, imp);
			std::string out;
			if (!GenerateCSharp(path, &out, &e)) return false;
			r = { { "file", out } };
			return true;
		}
		else
		{
			e = "unknown op '" + op + "' (nova inputactions help)";
			return false;
		}
		if (changed)
		{
			if (!asset.Save(path, &e)) return false;
			OnAssetSaved(path);
			if (InputActionsEditorWindow::IsOpen()) InputActionsEditorWindow::Open(path);   // 열린 창은 다시 읽는다
		}
		return true;
	}

	// ---------------------------------------------------------------- nova input (Play 중 입력 넣기 · 동작 보기)
	bool InputCli(const json& a, json& r, std::string& e)
	{
		const std::string op = a.value("op", std::string("help"));
		const int frames = a.value("frames", 0);
		const bool tap = a.value("tap", false);
		const bool up = a.value("up", false);
		const int hold = tap ? (std::max)(1, frames) : frames;   // --tap = 한 프레임 (또는 --frames)
		auto stamp = [](int n) { return n > 0 ? n + 1 : 0; };    // 다음 Poll 에서 하나 빼고 n 프레임 보인다
		if (op == "help")
		{
			r = { { "ops", json::array({
				"key <w|space|leftShift|1|f1|…> [--down (default) | --up | --tap] [--frames N]",
				"mouse [--position x,y | --position none] [--delta dx,dy] [--scroll y] [--button left|right|middle|back|forward --down|--up|--tap] [--frames N]",
				"gamepad [--index 0..3] [--leftStick x,y] [--rightStick x,y] [--leftTrigger v] [--rightTrigger v] [--button buttonSouth|a|dpad/up|… --down|--up|--tap] [--frames N] [--disconnect]",
				"release   (all injected input off)", "state   (injected input · gamepads · cursor)",
				"info   (C#: devices · enabled actions · PlayerInput)", "action <Map/Action>   (phase · value · counts)",
				"rebind <Map/Action> [--index i] [--group Keyboard&Mouse]   (next injected key/button becomes the binding)" }) } };
			return true;
		}
		if (op == "key")
		{
			const std::string name = a.value("path", a.value("name", std::string()));
			const int vk = KeyVk(name);
			if (vk == 0) { e = "unknown key '" + name + "' (Unity control names: w, space, leftShift, enter, escape, 1, f1, upArrow …)"; return false; }
			InputDevices::InjectKey(vk, !up, up ? 0 : stamp(hold));
			r = { { "key", name }, { "vk", vk }, { "down", !up }, { "frames", hold } };
			return true;
		}
		if (op == "mouse")
		{
			float x = 0, y = 0;
			if (a.contains("position") && a["position"].is_string() && Lower(a["position"].get<std::string>()) == "none")
				InputDevices::InjectMousePosition(false);
			else if (Vec2Arg(a, "position", x, y))
				InputDevices::InjectMousePosition(true, x, y);
			if (Vec2Arg(a, "delta", x, y))
				InputDevices::InjectMouseDelta(x, y, stamp((std::max)(1, frames)));
			if (a.contains("scroll") && a["scroll"].is_number())
				InputDevices::InjectScroll(a["scroll"].get<float>(), stamp((std::max)(1, frames)));
			if (a.contains("button"))
			{
				const int vk = MouseVk(a["button"].get<std::string>());
				if (vk == 0) { e = "unknown mouse button (left, right, middle, back, forward)"; return false; }
				InputDevices::InjectKey(vk, !up, up ? 0 : stamp(hold));
			}
			r = json::parse(InputDevices::Describe());
			return true;
		}
		if (op == "gamepad")
		{
			const int index = std::clamp(a.value("index", 0), 0, 3);
			InputDevices::PadState s = InputDevices::InjectedPad(index);
			if (a.value("disconnect", false))
			{
				InputDevices::InjectPad(index, InputDevices::PadState());
				r = { { "index", index }, { "connected", false } };
				return true;
			}
			s.Connected = true;
			InputDevices::PadRelease rel;   // --frames · --tap 뒤 놓을 것 (이번에 바꾼 것만)
			float x = 0, y = 0;
			if (Vec2Arg(a, "leftStick", x, y)) { s.LX = x; s.LY = y; rel.Sticks = true; }
			if (Vec2Arg(a, "rightStick", x, y)) { s.RX = x; s.RY = y; rel.Sticks = true; }
			if (a.contains("leftTrigger") && a["leftTrigger"].is_number()) { s.LT = a["leftTrigger"].get<float>(); rel.LT = true; }
			if (a.contains("rightTrigger") && a["rightTrigger"].is_number()) { s.RT = a["rightTrigger"].get<float>(); rel.RT = true; }
			if (a.contains("button"))
			{
				const std::string b = a["button"].get<std::string>();
				if (Lower(b) == "lefttrigger") { s.LT = up ? 0.0f : 1.0f; rel.LT = true; }
				else if (Lower(b) == "righttrigger") { s.RT = up ? 0.0f : 1.0f; rel.RT = true; }
				else
				{
					const uint32 bit = PadBit(b);
					if (bit == 0) { e = "unknown gamepad button '" + b + "' (buttonSouth/a, buttonEast/b, buttonWest/x, buttonNorth/y, leftShoulder, rightShoulder, start, select, leftStickPress, rightStickPress, dpad/up …, leftTrigger, rightTrigger)"; return false; }
					s.Buttons = up ? (s.Buttons & ~bit) : (s.Buttons | bit);
					rel.Buttons |= bit;
				}
			}
			InputDevices::InjectPad(index, s, up ? 0 : stamp(hold), rel);
			r = { { "index", index }, { "slot", 4 + index }, { "buttons", s.Buttons }, { "leftStick", { s.LX, s.LY } }, { "rightStick", { s.RX, s.RY } }, { "triggers", { s.LT, s.RT } } };
			return true;
		}
		if (op == "release")
		{
			InputDevices::ReleaseAll();
			r = { { "released", true } };
			return true;
		}
		if (op == "state")
		{
			r = json::parse(InputDevices::Describe());
			return true;
		}
		if (op == "info" || op == "action" || op == "rebind")
		{
			json req = a;
			if (op != "info" && !req.contains("name") && req.contains("path")) req["name"] = req["path"];
			if (req.contains("name") && req["name"].is_string())
			{
				std::string n = req["name"].get<std::string>();
				std::replace(n.begin(), n.end(), '\\', '/');
				req["name"] = n;
			}
			const std::string out = ScriptEngine::InputCommand(req.dump());
			const json j = json::parse(out, nullptr, false);
			if (j.is_discarded()) { e = "bad reply: " + out; return false; }
			if (j.contains("error")) { e = j["error"].get<std::string>(); return false; }
			r = j;
			return true;
		}
		e = "unknown op '" + op + "' (nova input help)";
		return false;
	}
}

// ==================================================================== 창
InputActionsEditorWindow::InputActionsEditorWindow()
	: EditorWindow("Input Actions", ICON_FA_GAMEPAD)
{
	s_Instance = this;
	SetIsOpened(false);
}

void InputActionsEditorWindow::Open(const std::string& path)
{
	if (s_Instance == nullptr)
		return;
	if (path.empty())
	{
		s_Instance->SetIsOpened(false);
		return;
	}
	if (s_Instance->m_Dirty && s_Instance->m_Path != path)
		s_Instance->Save();   // 다른 에셋을 열면 지금 것은 저장
	s_Instance->m_Path = path;
	s_Instance->m_Loaded = false;
	s_Instance->m_Dirty = false;
	s_Instance->m_Undo.clear();
	s_Instance->SetIsOpened(true);
	s_Instance->m_FocusNext = true;
	EditorLog::Write("Input", "Input Actions window: %s", path.c_str());
}

bool InputActionsEditorWindow::IsOpen() { return s_Instance && s_Instance->GetIsOpened(); }

bool InputActionsEditorWindow::Select(const std::string& action, int bindingIndex)
{
	if (s_Instance == nullptr || !s_Instance->EnsureLoaded())
		return false;
	Map* map = nullptr;
	Action* act = s_Instance->m_Asset.FindAction(action, &map);
	if (act == nullptr)
		return false;
	s_Instance->m_Map = (int)(map - s_Instance->m_Asset.Maps.data());
	s_Instance->m_Action = map->FindAction(act->Name);
	s_Instance->m_Binding = -1;
	const std::vector<int> idx = map->BindingsOf(act->Name);
	if (bindingIndex >= 0 && bindingIndex < (int)idx.size())
		s_Instance->m_Binding = idx[(size_t)bindingIndex];
	s_Instance->m_Collapsed.erase(act->Id);
	return true;
}

void InputActionsEditorWindow::BeforeBegin()
{
	const ImGuiViewport* vp = ImGui::GetMainViewport();
	ImGui::SetNextWindowSize(ImVec2(1040.0f, 600.0f), ImGuiCond_FirstUseEver);
	ImGui::SetNextWindowPos(ImVec2(vp->Pos.x + vp->Size.x * 0.5f, vp->Pos.y + vp->Size.y * 0.5f), ImGuiCond_FirstUseEver, ImVec2(0.5f, 0.5f));
	if (m_FocusNext)
	{
		ImGui::SetNextWindowFocus();
		m_FocusNext = false;
	}
}

void InputActionsEditorWindow::PushStyle() { ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0, 0)); }
void InputActionsEditorWindow::PopStyle() { ImGui::PopStyleVar(); }

bool InputActionsEditorWindow::EnsureLoaded()
{
	if (m_Loaded)
		return true;
	if (m_Path.empty())
		return false;
	std::string err;
	if (!m_Asset.Load(m_Path, &err))
		return false;
	m_Loaded = true;
	m_Map = m_Asset.Maps.empty() ? -1 : 0;
	m_Action = -1;
	m_Binding = -1;
	if (m_Map >= 0 && !m_Asset.Maps[0].Actions.empty())
		m_Action = 0;
	return true;
}

void InputActionsEditorWindow::Save()
{
	if (!m_Loaded)
		return;
	std::string err;
	if (m_Asset.Save(m_Path, &err))
	{
		m_Dirty = false;
		OnAssetSaved(m_Path);
		EditorLog::Write("Input", "saved %s", m_Path.c_str());
	}
	else
		Debug::LogWarning("Input Actions: " + err);
}

void InputActionsEditorWindow::PushUndo()
{
	m_Undo.push_back(m_Asset);
	if (m_Undo.size() > 64)
		m_Undo.erase(m_Undo.begin());
}

void InputActionsEditorWindow::Changed()
{
	m_Dirty = true;
	if (m_AutoSave)
		Save();
}

bool InputActionsEditorWindow::BindingVisible(const Map& map, int index) const
{
	const Binding& b = map.Bindings[(size_t)index];
	const std::string search = m_Search;
	if (b.IsComposite)
	{
		bool any = map.PartsOf(index).empty();
		for (int p : map.PartsOf(index))
			any |= BindingVisible(map, p);
		return any;
	}
	if (m_Scheme >= 0 && m_Scheme < (int)m_Asset.Schemes.size() && !HasGroup(b.Groups, m_Asset.Schemes[(size_t)m_Scheme].BindingGroup))
		return false;
	return true;
}

void InputActionsEditorWindow::OnRender()
{
	ImDrawList* dl = ImGui::GetWindowDrawList();
	const ImVec2 origin = ImGui::GetCursorScreenPos();
	const ImVec2 avail = ImGui::GetContentRegionAvail();
	if (!EnsureLoaded())
	{
		ImGui::SetCursorScreenPos(ImVec2(origin.x + 12, origin.y + 12));
		ImGui::TextDisabled(m_Path.empty() ? "Open an Input Actions asset (.inputactions) from the Project window, or Project > Create > Input Actions." : ("Cannot read " + m_Path).c_str());
		return;
	}
	// Ctrl+S · Ctrl+Z (이 창이 포커스일 때)
	if (ImGui::IsWindowFocused(ImGuiFocusedFlags_RootAndChildWindows) && !ImGui::GetIO().WantTextInput)
	{
		if (ImGui::GetIO().KeyCtrl && ImGui::IsKeyPressed(ImGuiKey_S)) Save();
		if (ImGui::GetIO().KeyCtrl && ImGui::IsKeyPressed(ImGuiKey_Z) && !m_Undo.empty())
		{
			m_Asset = m_Undo.back();
			m_Undo.pop_back();
			m_Map = std::clamp(m_Map, -1, (int)m_Asset.Maps.size() - 1);
			m_Action = -1;
			m_Binding = -1;
			Changed();
		}
	}
	dl->AddRectFilled(origin, ImVec2(origin.x + avail.x, origin.y + avail.y), kPanel);
	DrawToolbar(avail.x);
	const float bodyY = origin.y + 28.0f, bodyH = avail.y - 28.0f;
	const float mapsW = 210.0f, propsW = (std::min)(380.0f, (std::max)(300.0f, avail.x * 0.34f));
	const float actionsW = (std::max)(200.0f, avail.x - mapsW - propsW - 2.0f);
	ImGui::SetCursorScreenPos(ImVec2(origin.x, bodyY));
	ImGui::BeginChild("##iamaps", ImVec2(mapsW, bodyH), false);
	DrawMaps(bodyH);
	ImGui::EndChild();
	ImGui::SetCursorScreenPos(ImVec2(origin.x + mapsW + 1.0f, bodyY));
	ImGui::BeginChild("##iaactions", ImVec2(actionsW, bodyH), false);
	DrawActions(bodyH);
	ImGui::EndChild();
	ImGui::SetCursorScreenPos(ImVec2(origin.x + mapsW + actionsW + 2.0f, bodyY));
	ImGui::BeginChild("##iaprops", ImVec2(propsW, bodyH), false);
	DrawProperties(bodyH);
	ImGui::EndChild();
	dl->AddLine(ImVec2(origin.x + mapsW, bodyY), ImVec2(origin.x + mapsW, bodyY + bodyH), kBorder, 1.0f);
	dl->AddLine(ImVec2(origin.x + mapsW + actionsW + 1.0f, bodyY), ImVec2(origin.x + mapsW + actionsW + 1.0f, bodyY + bodyH), kBorder, 1.0f);
	DrawSchemeEditor();
}

void InputActionsEditorWindow::DrawToolbar(float width)
{
	ImDrawList* dl = ImGui::GetWindowDrawList();
	const ImVec2 p = ImGui::GetCursorScreenPos();
	dl->AddRectFilled(p, ImVec2(p.x + width, p.y + 27), kHeader);
	dl->AddLine(ImVec2(p.x, p.y + 27), ImVec2(p.x + width, p.y + 27), kBorder);
	ImGui::SetCursorScreenPos(ImVec2(p.x + 6, p.y + 3));
	ImGui::BeginDisabled(!m_Dirty);
	if (ImGui::Button("Save Asset"))
		Save();
	ImGui::EndDisabled();
	ImGui::SameLine();
	if (ImGui::Checkbox("Auto-Save", &m_AutoSave) && m_AutoSave && m_Dirty)
		Save();
	ImGui::SameLine(0, 18);
	// 컨트롤 스킴
	std::string current = m_Scheme >= 0 && m_Scheme < (int)m_Asset.Schemes.size() ? m_Asset.Schemes[(size_t)m_Scheme].Name : "All Control Schemes";
	ImGui::SetNextItemWidth(190);
	if (ImGui::BeginCombo("##scheme", current.c_str()))
	{
		if (ImGui::Selectable("All Control Schemes", m_Scheme < 0)) m_Scheme = -1;
		for (int i = 0; i < (int)m_Asset.Schemes.size(); ++i)
			if (ImGui::Selectable(m_Asset.Schemes[(size_t)i].Name.c_str(), m_Scheme == i)) m_Scheme = i;
		ImGui::Separator();
		if (ImGui::Selectable("Add Control Scheme..."))
		{
			m_SchemeEditIndex = -1;
			m_SchemeEdit = ControlScheme();
			std::vector<std::string> names;
			for (const auto& s : m_Asset.Schemes) names.push_back(s.Name);
			m_SchemeEdit.Name = UniqueName(names, "New control scheme");
			m_SchemeEditOpen = true;
		}
		ImGui::BeginDisabled(m_Scheme < 0);
		if (ImGui::Selectable("Edit Control Scheme...") && m_Scheme >= 0)
		{
			m_SchemeEditIndex = m_Scheme;
			m_SchemeEdit = m_Asset.Schemes[(size_t)m_Scheme];
			m_SchemeEditOpen = true;
		}
		if (ImGui::Selectable("Duplicate Control Scheme") && m_Scheme >= 0)
		{
			PushUndo();
			ControlScheme s = m_Asset.Schemes[(size_t)m_Scheme];
			std::vector<std::string> names;
			for (const auto& x : m_Asset.Schemes) names.push_back(x.Name);
			s.Name = s.BindingGroup = UniqueName(names, s.Name);
			m_Asset.Schemes.push_back(s);
			Changed();
		}
		if (ImGui::Selectable("Delete Control Scheme") && m_Scheme >= 0)
		{
			PushUndo();
			m_Asset.Schemes.erase(m_Asset.Schemes.begin() + m_Scheme);
			m_Scheme = -1;
			Changed();
		}
		ImGui::EndDisabled();
		ImGui::EndCombo();
	}
	ImGui::SameLine(0, 18);
	ImGui::SetNextItemWidth(200);
	ImGui::InputTextWithHint("##iasearch", ICON_FA_MAGNIFYING_GLASS " Search", m_Search, sizeof(m_Search));
	const std::string name = fs::path(string_to_wstring(m_Path)).filename().string() + (m_Dirty ? " *" : "");
	const ImVec2 ts = ImGui::CalcTextSize(name.c_str());
	dl->AddText(ImVec2(p.x + width - ts.x - 10, p.y + 6), kDim, name.c_str());
	ImGui::SetCursorScreenPos(ImVec2(p.x, p.y + 28));
}

void InputActionsEditorWindow::DrawMaps(float height)
{
	const float w = ImGui::GetContentRegionAvail().x;
	const ImVec2 head = ImGui::GetCursorScreenPos();
	SectionHeader("Action Maps", w, 24);
	if (HeaderPlus("addmap", head.x + w, head.y, 24))
	{
		PushUndo();
		Map m{ UniqueName(MapNames(m_Asset), "New action map"), NewGuid(), {}, {} };
		Action act;
		act.Name = "New action";
		act.Id = NewGuid();
		m.Actions.push_back(act);
		m.Bindings.push_back(MakeBinding(act.Name, ""));
		m_Asset.Maps.push_back(m);
		m_Map = (int)m_Asset.Maps.size() - 1;
		m_Action = 0;
		m_Binding = -1;
		m_RenameKind = 1;
		m_RenameIndex = m_Map;
		strncpy_s(m_RenameBuf, m.Name.c_str(), _TRUNCATE);
		m_RenameFrames = 0;
		Changed();
	}
	int remove = -1, duplicate = -1;
	for (int i = 0; i < (int)m_Asset.Maps.size(); ++i)
	{
		Map& m = m_Asset.Maps[(size_t)i];
		ImGui::PushID(i);
		if (m_RenameKind == 1 && m_RenameIndex == i)
		{
			ImGui::SetCursorPosX(8);
			ImGui::SetNextItemWidth(w - 16);
			if (m_RenameFrames++ == 0) ImGui::SetKeyboardFocusHere();
			const bool done = ImGui::InputText("##rename", m_RenameBuf, sizeof(m_RenameBuf), ImGuiInputTextFlags_EnterReturnsTrue | ImGuiInputTextFlags_AutoSelectAll);
			if (done || (m_RenameFrames > 2 && !ImGui::IsItemActive()))
			{
				std::string n = m_RenameBuf;
				if (!n.empty() && n != m.Name && m_Asset.FindMap(n) < 0)
				{
					PushUndo();
					m.Name = n;
					Changed();
				}
				m_RenameKind = 0;
			}
		}
		else
		{
			ImGui::SetCursorPosX(4);
			if (ImGui::Selectable(m.Name.c_str(), m_Map == i, ImGuiSelectableFlags_AllowDoubleClick, ImVec2(w - 8, 0)))
			{
				if (m_Map != i) { m_Map = i; m_Action = m.Actions.empty() ? -1 : 0; m_Binding = -1; }
				if (ImGui::IsMouseDoubleClicked(ImGuiMouseButton_Left))
				{
					m_RenameKind = 1; m_RenameIndex = i; m_RenameFrames = 0;
					strncpy_s(m_RenameBuf, m.Name.c_str(), _TRUNCATE);
				}
			}
			if (ImGui::BeginPopupContextItem("##mapctx"))
			{
				if (ImGui::MenuItem("Rename")) { m_RenameKind = 1; m_RenameIndex = i; m_RenameFrames = 0; strncpy_s(m_RenameBuf, m.Name.c_str(), _TRUNCATE); }
				if (ImGui::MenuItem("Duplicate")) duplicate = i;
				if (ImGui::MenuItem("Delete")) remove = i;
				ImGui::EndPopup();
			}
		}
		ImGui::PopID();
	}
	if (duplicate >= 0)
	{
		PushUndo();
		Map copy = m_Asset.Maps[(size_t)duplicate];
		copy.Name = UniqueName(MapNames(m_Asset), copy.Name);
		copy.Id = NewGuid();
		for (auto& a : copy.Actions) a.Id = NewGuid();
		for (auto& b : copy.Bindings) b.Id = NewGuid();
		m_Asset.Maps.insert(m_Asset.Maps.begin() + duplicate + 1, copy);
		m_Map = duplicate + 1;
		Changed();
	}
	if (remove >= 0)
	{
		PushUndo();
		m_Asset.Maps.erase(m_Asset.Maps.begin() + remove);
		m_Map = std::clamp(m_Map, -1, (int)m_Asset.Maps.size() - 1);
		m_Action = -1;
		m_Binding = -1;
		Changed();
	}
	(void)height;
}

void InputActionsEditorWindow::AddBindingMenu(Map& map, const std::string& action)
{
	const std::string group = m_Scheme >= 0 && m_Scheme < (int)m_Asset.Schemes.size() ? m_Asset.Schemes[(size_t)m_Scheme].BindingGroup : std::string();
	auto insert = [&](std::vector<Binding> add) {
		PushUndo();
		for (Binding& b : add)
			if (!b.IsComposite && !group.empty()) b.Groups = group;   // 고른 스킴에 넣는다 (Unity 처럼)
		const int at = map.InsertPointFor(action);
		map.Bindings.insert(map.Bindings.begin() + at, add.begin(), add.end());
		m_Binding = at;
		m_Action = map.FindAction(action);
		m_Collapsed.erase(map.Actions[(size_t)m_Action].Id);
		Changed();
	};
	if (ImGui::MenuItem("Add Binding"))
		insert({ MakeBinding(action, "") });
	for (const CompositeInfo& c : Composites())
	{
		std::string label;
		if (std::string(c.Name) == "1DAxis") label = "Add Positive\\Negative Binding";
		else if (std::string(c.Name) == "2DVector") label = "Add Up\\Down\\Left\\Right Composite";
		else if (std::string(c.Name) == "3DVector") label = "Add Up\\Down\\Left\\Right\\Forward\\Backward Composite";
		else label = std::string("Add ") + c.Display + (std::string(c.Name).find("Button") == 0 ? "" : " Composite");
		if (ImGui::MenuItem(label.c_str()))
		{
			std::vector<Binding> add = { MakeBinding(action, c.Name, c.Display, true) };
			for (const char* part : c.Parts)
				add.push_back(MakeBinding(action, "", part, false, true));
			insert(add);
		}
	}
}

void InputActionsEditorWindow::DrawActions(float height)
{
	const float w = ImGui::GetContentRegionAvail().x;
	const ImVec2 head = ImGui::GetCursorScreenPos();
	SectionHeader("Actions", w, 24);
	if (m_Map < 0 || m_Map >= (int)m_Asset.Maps.size())
	{
		ImGui::SetCursorPosX(10);
		ImGui::TextDisabled("Add an action map (+ on the left).");
		return;
	}
	Map& map = m_Asset.Maps[(size_t)m_Map];
	if (HeaderPlus("addaction", head.x + w, head.y, 24))
	{
		PushUndo();
		Action act;
		act.Name = UniqueName(ActionNames(map), "New action");
		act.Id = NewGuid();
		map.Actions.push_back(act);
		map.Bindings.push_back(MakeBinding(act.Name, ""));
		m_Action = (int)map.Actions.size() - 1;
		m_Binding = -1;
		m_RenameKind = 2;
		m_RenameIndex = m_Action;
		m_RenameFrames = 0;
		strncpy_s(m_RenameBuf, act.Name.c_str(), _TRUNCATE);
		Changed();
	}
	ImGui::BeginChild("##iaactionlist", ImVec2(0, 0), false);
	const std::string search = m_Search;
	int removeAction = -1, dupAction = -1, removeBinding = -1, dupBinding = -1;
	const float rowW = ImGui::GetContentRegionAvail().x;
	for (int ai = 0; ai < (int)map.Actions.size(); ++ai)
	{
		Action& act = map.Actions[(size_t)ai];
		const std::vector<int> binds = map.BindingsOf(act.Name);
		bool match = Contains(act.Name, search);
		if (!match)
			for (int bi : binds) match |= Contains(map.Bindings[(size_t)bi].Path, search) || Contains(DisplayPath(map.Bindings[(size_t)bi].Path), search);
		if (!match)
			continue;
		ImGui::PushID(act.Id.c_str());
		const bool collapsed = m_Collapsed.count(act.Id) > 0;
		const bool selected = m_Action == ai && m_Binding < 0;
		ImGui::SetCursorPosX(4);
		// 펼치기 화살표
		if (ImGui::ArrowButton("##fold", collapsed ? ImGuiDir_Right : ImGuiDir_Down))
		{
			if (collapsed) m_Collapsed.erase(act.Id); else m_Collapsed.insert(act.Id);
		}
		ImGui::SameLine(0, 4);
		if (m_RenameKind == 2 && m_RenameIndex == ai)
		{
			ImGui::SetNextItemWidth(rowW - 60);
			if (m_RenameFrames++ == 0) ImGui::SetKeyboardFocusHere();
			const bool done = ImGui::InputText("##rename", m_RenameBuf, sizeof(m_RenameBuf), ImGuiInputTextFlags_EnterReturnsTrue | ImGuiInputTextFlags_AutoSelectAll);
			if (done || (m_RenameFrames > 2 && !ImGui::IsItemActive()))
			{
				const std::string n = m_RenameBuf;
				if (!n.empty() && n != act.Name && map.FindAction(n) < 0)
				{
					PushUndo();
					map.RenameAction(act.Name, n);
					Changed();
				}
				m_RenameKind = 0;
			}
		}
		else
		{
			const std::string label = act.Name + "##act";
			if (ImGui::Selectable(label.c_str(), selected, ImGuiSelectableFlags_AllowDoubleClick | ImGuiSelectableFlags_AllowOverlap, ImVec2(rowW - 70, 0)))
			{
				m_Action = ai;
				m_Binding = -1;
				m_Listening = false;
				if (ImGui::IsMouseDoubleClicked(ImGuiMouseButton_Left))
				{
					m_RenameKind = 2; m_RenameIndex = ai; m_RenameFrames = 0;
					strncpy_s(m_RenameBuf, act.Name.c_str(), _TRUNCATE);
				}
			}
			if (ImGui::BeginPopupContextItem("##actctx"))
			{
				if (ImGui::MenuItem("Rename")) { m_RenameKind = 2; m_RenameIndex = ai; m_RenameFrames = 0; strncpy_s(m_RenameBuf, act.Name.c_str(), _TRUNCATE); }
				if (ImGui::MenuItem("Duplicate")) dupAction = ai;
				if (ImGui::MenuItem("Delete")) removeAction = ai;
				ImGui::Separator();
				AddBindingMenu(map, act.Name);
				ImGui::EndPopup();
			}
			ImGui::SameLine(rowW - 28);
			if (ImGui::SmallButton("+##addbind"))
				ImGui::OpenPopup("##addbindmenu");
			if (ImGui::BeginPopup("##addbindmenu"))
			{
				AddBindingMenu(map, act.Name);
				ImGui::EndPopup();
			}
		}
		if (!collapsed)
		{
			for (size_t k = 0; k < binds.size(); ++k)
			{
				const int bi = binds[k];
				Binding& b = map.Bindings[(size_t)bi];
				if (b.IsPartOfComposite)
					continue;   // 합성 아래에서 그린다
				if (!BindingVisible(map, bi))
					continue;
				ImGui::PushID(b.Id.c_str());
				const bool bsel = m_Action == ai && m_Binding == bi;
				ImGui::SetCursorPosX(30);
				std::string label = b.IsComposite ? (b.Name.empty() ? DisplayPath(b.Path) : b.Name) : DisplayPath(b.Path);
				if (ImGui::Selectable((label + "##b").c_str(), bsel, 0, ImVec2(rowW - 40, 0)))
				{
					m_Action = ai;
					m_Binding = bi;
					m_Listening = false;
				}
				if (ImGui::BeginPopupContextItem("##bctx"))
				{
					if (ImGui::MenuItem("Duplicate")) dupBinding = bi;
					if (ImGui::MenuItem("Delete")) removeBinding = bi;
					ImGui::EndPopup();
				}
				if (b.IsComposite)
					for (int p : map.PartsOf(bi))
					{
						if (!BindingVisible(map, p))
							continue;
						const Binding& pb = map.Bindings[(size_t)p];
						ImGui::PushID(pb.Id.c_str());
						ImGui::SetCursorPosX(52);
						std::string pl = pb.Name;
						if (!pl.empty()) pl[0] = (char)std::toupper((unsigned char)pl[0]);
						pl += ": " + DisplayPath(pb.Path);
						if (ImGui::Selectable((pl + "##p").c_str(), m_Action == ai && m_Binding == p, 0, ImVec2(rowW - 62, 0)))
						{
							m_Action = ai;
							m_Binding = p;
							m_Listening = false;
						}
						if (ImGui::BeginPopupContextItem("##pctx"))
						{
							if (ImGui::MenuItem("Delete")) removeBinding = p;
							ImGui::EndPopup();
						}
						ImGui::PopID();
					}
				ImGui::PopID();
			}
		}
		ImGui::PopID();
	}
	// Delete 키 (고른 것)
	if (ImGui::IsWindowFocused() && ImGui::IsKeyPressed(ImGuiKey_Delete) && !ImGui::GetIO().WantTextInput)
	{
		if (m_Binding >= 0) removeBinding = m_Binding;
		else if (m_Action >= 0) removeAction = m_Action;
	}
	if (removeBinding >= 0 && removeBinding < (int)map.Bindings.size())
	{
		PushUndo();
		map.RemoveBinding(removeBinding);
		m_Binding = -1;
		Changed();
	}
	else if (dupBinding >= 0)
	{
		PushUndo();
		const int end = map.Bindings[(size_t)dupBinding].IsComposite ? map.EndOfComposite(dupBinding) : dupBinding + 1;
		std::vector<Binding> copy(map.Bindings.begin() + dupBinding, map.Bindings.begin() + end);
		for (Binding& b : copy) b.Id = NewGuid();
		map.Bindings.insert(map.Bindings.begin() + end, copy.begin(), copy.end());
		m_Binding = end;
		Changed();
	}
	else if (removeAction >= 0)
	{
		PushUndo();
		map.RemoveAction(map.Actions[(size_t)removeAction].Name);
		m_Action = map.Actions.empty() ? -1 : (std::min)(removeAction, (int)map.Actions.size() - 1);
		m_Binding = -1;
		Changed();
	}
	else if (dupAction >= 0)
	{
		PushUndo();
		Action copy = map.Actions[(size_t)dupAction];
		const std::string oldName = copy.Name;
		copy.Name = UniqueName(ActionNames(map), copy.Name);
		copy.Id = NewGuid();
		std::vector<Binding> binds;
		for (int i : map.BindingsOf(oldName))
		{
			Binding b = map.Bindings[(size_t)i];
			b.Id = NewGuid();
			b.Action = copy.Name;
			binds.push_back(b);
		}
		map.Actions.insert(map.Actions.begin() + dupAction + 1, copy);
		map.Bindings.insert(map.Bindings.end(), binds.begin(), binds.end());
		m_Action = dupAction + 1;
		m_Binding = -1;
		Changed();
	}
	ImGui::EndChild();
	(void)height;
}

void InputActionsEditorWindow::DrawProperties(float height)
{
	const float w = ImGui::GetContentRegionAvail().x;
	if (m_Map < 0 || m_Map >= (int)m_Asset.Maps.size())
	{
		SectionHeader("Properties", w, 24);
		return;
	}
	Map& map = m_Asset.Maps[(size_t)m_Map];
	if (m_Action < 0 || m_Action >= (int)map.Actions.size())
	{
		SectionHeader("Properties", w, 24);
		ImGui::SetCursorPosX(10);
		ImGui::TextDisabled("Select an action or a binding.");
		return;
	}
	ImGui::BeginChild("##iapropbody", ImVec2(0, height), false);
	if (m_Binding >= 0 && m_Binding < (int)map.Bindings.size())
	{
		if (map.Bindings[(size_t)m_Binding].IsComposite)
		{
			SectionHeader("Composite Properties", w, 24);
			DrawCompositeProperties(map, m_Binding);
		}
		else
		{
			SectionHeader("Binding Properties", w, 24);
			DrawBindingProperties(map, m_Binding);
		}
	}
	else
	{
		SectionHeader("Action Properties", w, 24);
		DrawActionProperties(map, map.Actions[(size_t)m_Action]);
	}
	ImGui::EndChild();
}

void InputActionsEditorWindow::DrawActionProperties(Map& map, Action& act)
{
	(void)map;
	UnityGUI::Spacing(4);
	UnityGUI::Label("Action", 0, true);
	static const char* kTypes[] = { "Value", "Button", "Pass Through" };
	int type = act.Type == "Value" ? 0 : (act.Type == "PassThrough" ? 2 : 1);
	if (UnityGUI::Dropdown("Action Type", &type, kTypes, 3))
	{
		PushUndo();
		act.Type = type == 0 ? "Value" : (type == 2 ? "PassThrough" : "Button");
		if (act.Type == "Button") act.ExpectedControlType = "Button";
		else if (act.ExpectedControlType == "Button" && act.Type == "Value") act.ExpectedControlType = "";
		if (act.Type == "Value") act.InitialStateCheck = true;
		Changed();
	}
	if (act.Type != "Button")
	{
		static const char* kControls[] = { "Any", "Analog", "Axis", "Button", "Digital", "Double", "Dpad", "Integer", "Quaternion", "Stick", "Touch", "Vector2", "Vector3" };
		int ci = 0;
		for (int i = 1; i < (int)(sizeof(kControls) / sizeof(kControls[0])); ++i)
			if (Lower(act.ExpectedControlType) == Lower(kControls[i])) ci = i;
		if (UnityGUI::Dropdown("Control Type", &ci, kControls, (int)(sizeof(kControls) / sizeof(kControls[0]))))
		{
			PushUndo();
			act.ExpectedControlType = ci == 0 ? std::string() : kControls[ci];
			Changed();
		}
	}
	if (act.Type != "Value")
	{
		bool initial = act.InitialStateCheck;
		if (UnityGUI::Toggle("Initial State Check", &initial))
		{
			PushUndo();
			act.InitialStateCheck = initial;
			Changed();
		}
	}
	else
		UnityGUI::HelpBox("Value actions check the current state when enabled (a stick already pushed moves at once).", false);
	const std::string valueType = act.ExpectedControlType.empty() ? "" : (ControlTypeMatches("Vector2", act.ExpectedControlType) ? "Vector2" : (ControlTypeMatches("Vector3", act.ExpectedControlType) ? "Vector3" : "float"));
	if (NamedList("Interactions", act.Interactions, false, valueType)) Changed();
	if (NamedList("Processors", act.Processors, true, valueType)) Changed();
}

void InputActionsEditorWindow::DrawBindingProperties(Map& map, int index)
{
	Binding& b = map.Bindings[(size_t)index];
	const int ai = map.FindAction(b.Action);
	std::string expected = ai >= 0 ? map.Actions[(size_t)ai].ExpectedControlType : std::string();
	int composite = -1;
	if (b.IsPartOfComposite)
	{
		for (int c = index - 1; c >= 0; --c)
			if (!map.Bindings[(size_t)c].IsPartOfComposite) { composite = c; break; }
		expected = "";   // 부분은 단추 · 축 · 아무거나
	}
	UnityGUI::Spacing(4);
	UnityGUI::Label("Binding", 0, true);
	if (PathField(b.Path, expected)) Changed();
	if (composite >= 0)
	{
		if (const CompositeInfo* ci = FindComposite(map.Bindings[(size_t)composite].Path))
		{
			std::vector<const char*> parts(ci->Parts.begin(), ci->Parts.end());
			int pi = 0;
			for (int i = 0; i < (int)parts.size(); ++i)
				if (Lower(parts[(size_t)i]) == Lower(b.Name)) pi = i;
			if (UnityGUI::Dropdown("Composite Part", &pi, parts.data(), (int)parts.size()))
			{
				PushUndo();
				b.Name = parts[(size_t)pi];
				Changed();
			}
		}
	}
	if (!m_Asset.Schemes.empty())
	{
		UnityGUI::Spacing(6);
		// 라벨 칸보다 길어 UnityGUI::Label 로는 잘린다
		ImGui::SetCursorPosX(ImGui::GetCursorPosX() + UnityGUI::kBaseIndent);
		ImGui::PushFont(UnityGUI::BoldFont());
		ImGui::TextUnformatted("Use in control scheme");
		ImGui::PopFont();
		for (const ControlScheme& s : m_Asset.Schemes)
		{
			bool on = HasGroup(b.Groups, s.BindingGroup);
			if (UnityGUI::ToggleLeft(s.Name.c_str(), &on, 1))
			{
				PushUndo();
				b.Groups = SetGroup(b.Groups, s.BindingGroup, on);
				Changed();
			}
		}
	}
	const std::string valueType = expected.empty() ? "" : (ControlTypeMatches("Vector2", expected) ? "Vector2" : "float");
	if (!b.IsPartOfComposite && NamedList("Interactions", b.Interactions, false, valueType)) Changed();
	if (NamedList("Processors", b.Processors, true, b.IsPartOfComposite ? "float" : valueType)) Changed();
}

void InputActionsEditorWindow::DrawCompositeProperties(Map& map, int index)
{
	Binding& b = map.Bindings[(size_t)index];
	UnityGUI::Spacing(4);
	UnityGUI::Label("Composite", 0, true);
	std::string name = b.Name;
	if (UnityGUI::TextField("Name", &name))
	{
		b.Name = name;
		Changed();
	}
	const CompositeInfo* ci = FindComposite(b.Path);
	std::vector<const char*> types;
	int ti = 0;
	for (size_t i = 0; i < Composites().size(); ++i)
	{
		types.push_back(Composites()[i].Display);
		if (ci && std::string(Composites()[i].Name) == ci->Name) ti = (int)i;
	}
	if (UnityGUI::Dropdown("Composite Type", &ti, types.data(), (int)types.size()))
	{
		PushUndo();
		const CompositeInfo& nc = Composites()[(size_t)ti];
		b.Path = nc.Name;
		// 부분 이름을 새 종류에 맞춘다 (모자라면 더한다)
		std::vector<int> parts = map.PartsOf(index);
		for (size_t i = 0; i < parts.size() && i < nc.Parts.size(); ++i)
			map.Bindings[(size_t)parts[i]].Name = nc.Parts[i];
		for (size_t i = parts.size(); i < nc.Parts.size(); ++i)
			map.Bindings.insert(map.Bindings.begin() + map.EndOfComposite(index), MakeBinding(b.Action, "", nc.Parts[i], false, true));
		Changed();
		return;
	}
	if (ci)
	{
		const std::string n = ci->Name;
		if (n == "2DVector" || n == "3DVector")
		{
			// 2DVector: mode 0 Digital Normalized, 1 Digital, 2 Analog — 3DVector: 0 Analog, 1 Digital Normalized, 2 Digital (Unity 와 같은 번호)
			static const char* k2[] = { "Digital Normalized", "Digital", "Analog" };
			static const char* k3[] = { "Analog", "Digital Normalized", "Digital" };
			int mode = std::atoi(CompositeParam(b.Path, "mode", "0").c_str());
			if (UnityGUI::Dropdown("Mode", &mode, n == "2DVector" ? k2 : k3, 3))
			{
				PushUndo();
				b.Path = SetCompositeParam(b.Path, "mode", std::to_string(mode), "0");
				Changed();
			}
		}
		else if (n == "1DAxis")
		{
			float minV = (float)std::atof(CompositeParam(b.Path, "minValue", "-1").c_str());
			float maxV = (float)std::atof(CompositeParam(b.Path, "maxValue", "1").c_str());
			static const char* kSide[] = { "Neither", "Positive", "Negative" };
			int side = std::atoi(CompositeParam(b.Path, "whichSideWins", "0").c_str());
			char buf[32];
			if (UnityGUI::Float("Min Value", &minV)) { snprintf(buf, sizeof(buf), "%g", minV); b.Path = SetCompositeParam(b.Path, "minValue", buf, "-1"); Changed(); }
			if (UnityGUI::Float("Max Value", &maxV)) { snprintf(buf, sizeof(buf), "%g", maxV); b.Path = SetCompositeParam(b.Path, "maxValue", buf, "1"); Changed(); }
			if (UnityGUI::Dropdown("Which Side Wins", &side, kSide, 3)) { b.Path = SetCompositeParam(b.Path, "whichSideWins", std::to_string(side), "0"); Changed(); }
		}
		else
		{
			bool any = CompositeParam(b.Path, "overrideModifiersNeedToBePressedFirst", "false") == "true";
			if (UnityGUI::Toggle("Modifiers Any Order", &any))
			{
				b.Path = SetCompositeParam(b.Path, "overrideModifiersNeedToBePressedFirst", any ? "true" : "false", "false");
				Changed();
			}
		}
	}
	const int ai = map.FindAction(b.Action);
	const std::string expected = ai >= 0 ? map.Actions[(size_t)ai].ExpectedControlType : std::string();
	if (NamedList("Interactions", b.Interactions, false, "")) Changed();
	if (NamedList("Processors", b.Processors, true, ci ? (std::string(ci->ValueType) == "Vector2" ? "Vector2" : (std::string(ci->ValueType) == "Vector3" ? "Vector3" : "float")) : "")) Changed();
	(void)expected;
}

// Interactions · Processors: 목록 + 매개변수 + (+) 메뉴
bool InputActionsEditorWindow::NamedList(const char* title, std::string& text, bool processors, const std::string& valueType)
{
	bool changed = false;
	ImGui::PushID(title);
	UnityGUI::Spacing(8);
	const float w = ImGui::GetContentRegionAvail().x;
	const ImVec2 head = ImGui::GetCursorScreenPos();
	UnityGUI::Label(title, 0, true);
	ImGui::SetCursorScreenPos(ImVec2(head.x + w - 30, head.y));
	if (ImGui::SmallButton("+"))
		ImGui::OpenPopup("##addnamed");
	const std::vector<NamedInfo>& types = processors ? ProcessorTypes() : InteractionTypes();
	std::vector<NameAndParams> list = ParseList(text);
	if (ImGui::BeginPopup("##addnamed"))
	{
		for (const NamedInfo& t : types)
		{
			if (processors && !valueType.empty() && t.ValueType && std::string(t.ValueType) != valueType)
				continue;
			if (ImGui::MenuItem(t.Display))
			{
				PushUndo();
				list.push_back({ t.Name, {} });
				text = JoinList(list);
				changed = true;
			}
		}
		ImGui::EndPopup();
	}
	ImGui::SetCursorScreenPos(ImVec2(head.x, head.y + UnityGUI::kRowStep));
	if (list.empty())
	{
		ImGui::SetCursorPosX(ImGui::GetCursorPosX() + UnityGUI::kBaseIndent + 14.0f);
		ImGui::TextDisabled("%s", processors ? "No Processors have been added." : "No Interactions have been added.");
	}
	int remove = -1;
	for (size_t i = 0; i < list.size(); ++i)
	{
		ImGui::PushID((int)i);
		NameAndParams& n = list[i];
		const NamedInfo* info = FindNamed(types, n.Name);
		const ImVec2 row = ImGui::GetCursorScreenPos();
		UnityGUI::Label(info ? info->Display : n.Name.c_str(), 1, true);
		ImGui::SetCursorScreenPos(ImVec2(row.x + w - 30, row.y));
		if (ImGui::SmallButton("-"))
			remove = (int)i;
		ImGui::SetCursorScreenPos(ImVec2(row.x, row.y + UnityGUI::kRowStep));
		if (info)
			for (const ParamInfo& p : info->Params)
			{
				std::string cur = p.Default;
				for (const auto& kv : n.Params)
					if (Lower(kv.first) == Lower(p.Name)) cur = kv.second;
				std::string next = cur;
				const std::string type = p.Type;
				std::string nice = p.Name;
				if (!nice.empty()) nice[0] = (char)std::toupper((unsigned char)nice[0]);
				for (size_t k = 1; k < nice.size(); ++k)
					if (std::isupper((unsigned char)nice[k]) && nice[k - 1] != ' ') { nice.insert(k, " "); ++k; }
				if (type == "float")
				{
					float f = (float)std::atof(cur.c_str());
					if (UnityGUI::Float(nice.c_str(), &f, 2)) { char buf[32]; snprintf(buf, sizeof(buf), "%g", f); next = buf; }
					if (std::string(p.Default) == "0" && f == 0.0f && ImGui::IsItemHovered())
						ImGui::SetTooltip("0 = default (Project Settings > Input System Package)");
				}
				else if (type == "int")
				{
					int v = std::atoi(cur.c_str());
					if (UnityGUI::Int(nice.c_str(), &v, 2)) next = std::to_string(v);
				}
				else if (type == "bool")
				{
					bool v = cur == "true" || cur == "1";
					if (UnityGUI::Toggle(nice.c_str(), &v, 2)) next = v ? "true" : "false";
				}
				else if (type.rfind("enum:", 0) == 0)
				{
					std::vector<std::string> names;
					std::stringstream ss(type.substr(5));
					std::string e;
					while (std::getline(ss, e, ',')) names.push_back(e);
					std::vector<const char*> items;
					for (const auto& x : names) items.push_back(x.c_str());
					int v = std::atoi(cur.c_str());
					if (UnityGUI::Dropdown(nice.c_str(), &v, items.data(), (int)items.size(), 2)) next = std::to_string(v);
				}
				if (next != cur)
				{
					PushUndo();
					n.Params.erase(std::remove_if(n.Params.begin(), n.Params.end(), [&](const auto& kv) { return Lower(kv.first) == Lower(p.Name); }), n.Params.end());
					if (next != p.Default) n.Params.push_back({ p.Name, next });
					text = JoinList(list);
					changed = true;
				}
			}
		ImGui::PopID();
	}
	if (remove >= 0)
	{
		PushUndo();
		list.erase(list.begin() + remove);
		text = JoinList(list);
		changed = true;
	}
	ImGui::PopID();
	return changed;
}

// Path: [표시 ▾] [T] — ▾ = 컨트롤 고르기 (Listen · 검색 · 장치별 목록), T = 글자로
bool InputActionsEditorWindow::PathField(std::string& path, const std::string& expected)
{
	bool changed = false;
	ImGui::PushID("path");
	const float w = ImGui::GetContentRegionAvail().x;
	const ImVec2 row = ImGui::GetCursorScreenPos();
	const float labelW = (std::max)(110.0f, w * 0.38f);
	ImGui::SetCursorScreenPos(ImVec2(row.x + UnityGUI::kBaseIndent, row.y + 2));
	ImGui::TextUnformatted("Path");
	ImGui::SetCursorScreenPos(ImVec2(row.x + labelW, row.y));
	const float fieldW = w - labelW - 34;
	if (m_PathText)
	{
		char buf[256];
		strncpy_s(buf, path.c_str(), _TRUNCATE);
		ImGui::SetNextItemWidth(fieldW);
		if (ImGui::InputText("##pathtext", buf, sizeof(buf), ImGuiInputTextFlags_EnterReturnsTrue))
		{
			PushUndo();
			path = buf;
			changed = true;
		}
	}
	else
	{
		const std::string shown = path.empty() ? std::string("<No Binding>") : DisplayPath(path);
		if (ImGui::Button((shown + "##pathbtn").c_str(), ImVec2(fieldW, 0)))
		{
			ImGui::OpenPopup("##pathpicker");
			m_Listening = false;
			m_ListenCandidates.clear();
		}
	}
	ImGui::SameLine(0, 4);
	if (ImGui::Button(m_PathText ? "T*##pt" : "T##pt", ImVec2(26, 0)))
		m_PathText = !m_PathText;
	if (ImGui::IsItemHovered())
		ImGui::SetTooltip("Edit the control path as text (<Keyboard>/space, <Gamepad>/leftStick, */{Submit} ...)");
	ImGui::SetNextWindowSize(ImVec2(340, 420), ImGuiCond_Appearing);
	if (ImGui::BeginPopup("##pathpicker"))
	{
		static char s_Find[64] = {};
		if (ImGui::Button(m_Listening ? "Stop Listening" : "Listen", ImVec2(110, 0)))
		{
			m_Listening = !m_Listening;
			m_ListenCandidates.clear();
			memset(m_ListenKeys, 0, sizeof(m_ListenKeys));
			memset(m_ListenPads, 0, sizeof(m_ListenPads));
			memset(m_ListenAxes, 0, sizeof(m_ListenAxes));
			if (m_Listening)
			{
				// 지금 눌린 것은 기준으로 (새로 누른 것만 후보)
				for (int vk = 8; vk < 256; ++vk) m_ListenKeys[vk] = (::GetAsyncKeyState(vk) & 0x8000) ? 1 : 0;
				for (int i = 0; i < 4; ++i)
				{
					InputDevices::PadState p;
					if (InputDevices::ReadHardwarePad(i, p)) { m_ListenPads[i] = p.Buttons; float ax[6] = { p.LX, p.LY, p.RX, p.RY, p.LT, p.RT }; memcpy(m_ListenAxes[i], ax, sizeof(ax)); }
				}
			}
		}
		ImGui::SameLine();
		ImGui::SetNextItemWidth(-1);
		ImGui::InputTextWithHint("##find", "Search", s_Find, sizeof(s_Find));
		ImGui::Separator();
		ImGui::BeginChild("##pickerlist", ImVec2(0, 0), false);
		auto pick = [&](const std::string& p) {
			PushUndo();
			path = p;
			changed = true;
			m_Listening = false;
			ImGui::CloseCurrentPopup();
		};
		if (m_Listening)
		{
			UpdateListen(expected);
			ImGui::TextDisabled(m_ListenCandidates.empty() ? "Listening for input... (press a key or a gamepad button / stick)" : "Pick a control:");
			for (const std::string& c : m_ListenCandidates)
				if (ImGui::Selectable((DisplayPath(c) + "##" + c).c_str()))
				{
					pick(c);
					break;
				}
		}
		else
		{
			const std::string find = s_Find;
			if (ImGui::TreeNode("Usages"))
			{
				static const char* kUsages[] = { "Submit", "Cancel", "PrimaryAction", "SecondaryAction", "Back", "Menu", "Point", "Primary2DMotion", "Secondary2DMotion" };
				for (const char* u : kUsages)
					if (Contains(u, find) && ImGui::Selectable(u))
						pick(std::string("*/{") + u + "}");
				ImGui::TreePop();
			}
			for (const LayoutInfo& l : Layouts())
			{
				bool any = false;
				for (const ControlInfo& c : l.Controls)
					any |= ControlTypeMatches(expected, c.Layout) && (Contains(c.Display, find) || Contains(c.Path, find));
				if (!any)
					continue;
				if (!find.empty()) ImGui::SetNextItemOpen(true, ImGuiCond_Always);
				if (ImGui::TreeNode(l.Display.c_str()))
				{
					for (const ControlInfo& c : l.Controls)
					{
						if (!ControlTypeMatches(expected, c.Layout) || !(Contains(c.Display, find) || Contains(c.Path, find)))
							continue;
						if (ImGui::Selectable((c.Display + "##" + c.Path).c_str()))
							pick("<" + l.Name + ">/" + c.Path);
					}
					ImGui::TreePop();
				}
			}
		}
		ImGui::EndChild();
		ImGui::EndPopup();
	}
	else
		m_Listening = false;
	ImGui::SetCursorScreenPos(ImVec2(row.x, row.y + UnityGUI::kRowStep + 2));
	ImGui::PopID();
	return changed;
}

// Listen: 새로 누른 키 · 패드 단추 · 스틱 · 트리거 (마우스 단추는 창을 누르는 데 쓰여 뺀다)
void InputActionsEditorWindow::UpdateListen(const std::string& expected)
{
	auto add = [&](const std::string& p, const std::string& layout) {
		if (!ControlTypeMatches(expected, layout)) return;
		if (std::find(m_ListenCandidates.begin(), m_ListenCandidates.end(), p) == m_ListenCandidates.end())
			m_ListenCandidates.push_back(p);
	};
	for (int vk = 8; vk < 256; ++vk)
	{
		const uint8_t down = (::GetAsyncKeyState(vk) & 0x8000) ? 1 : 0;
		if (down && !m_ListenKeys[vk])
		{
			const std::string name = KeyNameFromVk(vk);
			if (!name.empty()) add("<Keyboard>/" + name, "Key");
		}
		m_ListenKeys[vk] = down;
	}
	for (int i = 0; i < 4; ++i)
	{
		InputDevices::PadState p;
		if (!InputDevices::ReadHardwarePad(i, p))
			continue;
		for (const auto& kb : kPadBits)
			if ((p.Buttons & kb.first) && !(m_ListenPads[i] & kb.first))
				add(std::string("<Gamepad>/") + kb.second, "Button");
		m_ListenPads[i] = p.Buttons;
		const float ax[6] = { p.LX, p.LY, p.RX, p.RY, p.LT, p.RT };
		const char* sticks[2] = { "leftStick", "rightStick" };
		for (int s = 0; s < 2; ++s)
		{
			const float x = ax[s * 2], y = ax[s * 2 + 1];
			const float px = m_ListenAxes[i][s * 2], py = m_ListenAxes[i][s * 2 + 1];
			if (x * x + y * y > 0.36f && px * px + py * py <= 0.36f)
			{
				add(std::string("<Gamepad>/") + sticks[s], "Stick");
				const char* dir = std::fabs(x) > std::fabs(y) ? (x > 0 ? "right" : "left") : (y > 0 ? "up" : "down");
				add(std::string("<Gamepad>/") + sticks[s] + "/" + dir, "Button");
			}
		}
		if (ax[4] > 0.5f && m_ListenAxes[i][4] <= 0.5f) add("<Gamepad>/leftTrigger", "Button");
		if (ax[5] > 0.5f && m_ListenAxes[i][5] <= 0.5f) add("<Gamepad>/rightTrigger", "Button");
		memcpy(m_ListenAxes[i], ax, sizeof(ax));
	}
}

void InputActionsEditorWindow::DrawSchemeEditor()
{
	if (!m_SchemeEditOpen)
		return;
	ImGui::SetNextWindowSize(ImVec2(420, 340), ImGuiCond_Appearing);
	bool open = true;
	if (ImGui::Begin(m_SchemeEditIndex < 0 ? "Add Control Scheme##ia" : "Edit Control Scheme##ia", &open, ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoDocking))
	{
		std::string name = m_SchemeEdit.Name;
		if (UnityGUI::TextField("Scheme Name", &name))
		{
			if (m_SchemeEdit.BindingGroup.empty() || m_SchemeEdit.BindingGroup == m_SchemeEdit.Name) m_SchemeEdit.BindingGroup = name;
			m_SchemeEdit.Name = name;
		}
		UnityGUI::Spacing(6);
		UnityGUI::Label("Devices", 0, true);
		static const char* kReq[] = { "Required", "Optional" };
		int remove = -1;
		for (size_t i = 0; i < m_SchemeEdit.Devices.size(); ++i)
		{
			ImGui::PushID((int)i);
			auto& d = m_SchemeEdit.Devices[i];
			int req = d.IsOptional ? 1 : 0;
			const std::string label = DisplayPath(d.DevicePath);
			if (UnityGUI::Dropdown(label.c_str(), &req, kReq, 2, 1)) d.IsOptional = req == 1;
			ImGui::SameLine();
			if (ImGui::SmallButton("-")) remove = (int)i;
			ImGui::PopID();
		}
		if (remove >= 0) m_SchemeEdit.Devices.erase(m_SchemeEdit.Devices.begin() + remove);
		if (m_SchemeEdit.Devices.empty()) UnityGUI::Label("List is Empty", 1);
		if (ImGui::Button("+ Add Device"))
			ImGui::OpenPopup("##adddev");
		if (ImGui::BeginPopup("##adddev"))
		{
			for (const LayoutInfo& l : Layouts())
				if (ImGui::MenuItem(l.Display.c_str()))
					m_SchemeEdit.Devices.push_back({ "<" + l.Name + ">", false, false });
			ImGui::EndPopup();
		}
		UnityGUI::Spacing(10);
		const bool nameOk = !m_SchemeEdit.Name.empty() && (m_Asset.FindScheme(m_SchemeEdit.Name) < 0 || m_Asset.FindScheme(m_SchemeEdit.Name) == m_SchemeEditIndex);
		ImGui::BeginDisabled(!nameOk);
		if (ImGui::Button(m_SchemeEditIndex < 0 ? "Add" : "Save", ImVec2(100, 0)))
		{
			PushUndo();
			if (m_SchemeEdit.BindingGroup.empty()) m_SchemeEdit.BindingGroup = m_SchemeEdit.Name;
			if (m_SchemeEditIndex < 0)
			{
				m_Asset.Schemes.push_back(m_SchemeEdit);
				m_Scheme = (int)m_Asset.Schemes.size() - 1;
			}
			else
			{
				// 이름 (바인딩 그룹) 이 바뀌면 바인딩의 그룹도
				const std::string oldGroup = m_Asset.Schemes[(size_t)m_SchemeEditIndex].BindingGroup;
				if (oldGroup != m_SchemeEdit.BindingGroup)
					for (Map& m : m_Asset.Maps)
						for (Binding& b : m.Bindings)
							if (HasGroup(b.Groups, oldGroup)) b.Groups = SetGroup(SetGroup(b.Groups, oldGroup, false), m_SchemeEdit.BindingGroup, true);
				m_Asset.Schemes[(size_t)m_SchemeEditIndex] = m_SchemeEdit;
			}
			Changed();
			open = false;
		}
		ImGui::EndDisabled();
		ImGui::SameLine();
		if (ImGui::Button("Cancel", ImVec2(100, 0)))
			open = false;
	}
	ImGui::End();
	if (!open)
		m_SchemeEditOpen = false;
}

// ==================================================================== Project Settings > Input System Package
void InputActionsEditorWindow::DrawProjectSettings()
{
	Settings& s = ProjectSettings();
	ImGui::PushFont(UnityGUI::HeaderFont());
	ImGui::TextUnformatted("Input System Package");
	ImGui::PopFont();
	ImGui::Spacing();
	bool changed = false;
	UnityGUI::Label("Project-wide Actions", 0, true);
	{
		const std::string text = s.ProjectWideActions.empty() ? std::string("None (Input Action Asset)") : fs::path(string_to_wstring(s.ProjectWideActions)).stem().string() + " (Input Action Asset)";
		ImVec2 fmin, fmax;
		const int pressed = UnityGUI::ObjectFieldButtons("Actions", text.c_str(), "input_actions", nullptr, 0, &fmin, &fmax, 1);
		const ImVec2 after = ImGui::GetCursorScreenPos();
		if (pressed == -1)
		{
			ObjectPicker::Options opt;
			opt.TypeName = "InputActionAsset";
			opt.Icon = "input_actions";
			opt.Items = FindAll();
			opt.Current = s.ProjectWideActions;
			ObjectPicker::Open("projectWideActions", std::move(opt));
		}
		std::string picked;
		if (ObjectPicker::Poll("projectWideActions", picked)) { s.ProjectWideActions = picked; changed = true; }
		ImGui::SetCursorScreenPos(fmin);
		ImGui::InvisibleButton("##pwdrop", ImVec2((std::max)(1.0f, fmax.x - fmin.x - 22.0f), fmax.y - fmin.y));
		if (ImGui::BeginDragDropTarget())
		{
			if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload("ASSET_FILE"))
			{
				const std::string dropped(static_cast<const char*>(payload->Data));
				if (IsInputActionsPath(dropped)) { s.ProjectWideActions = dropped; changed = true; }
			}
			ImGui::EndDragDropTarget();
		}
		ImGui::SetCursorScreenPos(after);
	}
	if (s.ProjectWideActions.empty())
	{
		UnityGUI::HelpBox("Project-wide actions are enabled when Play starts and read with InputSystem.actions (FindAction(\"Move\") ...). Unity 6 projects start with Assets/InputSystem_Actions.inputactions.", false, 1);
		if (UnityGUI::CenterButton("Create and assign a default project-wide Action Asset", 360.0f))
		{
			const std::string p = "Assets\\InputSystem_Actions.inputactions";
			std::error_code ec;
			if (!fs::exists(fs::path(FullPath(p)), ec))
				DefaultAsset("InputSystem_Actions").Save(p);
			s.ProjectWideActions = p;
			changed = true;
		}
	}
	else if (UnityGUI::CenterButton("Edit Project-wide Actions"))
		InputActionsEditorWindow::Open(s.ProjectWideActions);

	UnityGUI::Spacing(10);
	UnityGUI::Label("Settings", 0, true);
	static const char* kUpdate[] = { "Process Events In Dynamic Update", "Process Events In Fixed Update", "Process Events Manually" };
	int um = std::clamp(s.UpdateMode, 1, 3) - 1;
	if (UnityGUI::Dropdown("Update Mode", &um, kUpdate, 3, 1)) { s.UpdateMode = um + 1; changed = true; }
	if (s.UpdateMode == 2)
		UnityGUI::HelpBox("Fixed Update is processed once per frame like Dynamic Update in this version.", true, 1);
	static const char* kBg[] = { "Reset And Disable Non-Background Devices", "Reset And Disable All Devices", "Ignore Focus" };
	if (UnityGUI::Dropdown("Background Behavior", &s.BackgroundBehavior, kBg, 3, 1)) changed = true;
	UnityGUI::Spacing(4);
	changed |= UnityGUI::Float("Default Deadzone Min", &s.DefaultDeadzoneMin, 1);
	changed |= UnityGUI::Float("Default Deadzone Max", &s.DefaultDeadzoneMax, 1);
	changed |= UnityGUI::Float("Default Button Press Point", &s.DefaultButtonPressPoint, 1);
	changed |= UnityGUI::Float("Button Release Threshold", &s.ButtonReleaseThreshold, 1);
	changed |= UnityGUI::Float("Default Tap Time", &s.DefaultTapTime, 1);
	changed |= UnityGUI::Float("Default Slow Tap Time", &s.DefaultSlowTapTime, 1);
	changed |= UnityGUI::Float("Default Hold Time", &s.DefaultHoldTime, 1);
	changed |= UnityGUI::Float("Tap Radius", &s.TapRadius, 1);
	changed |= UnityGUI::Float("Multi Tap Delay Time", &s.MultiTapDelayTime, 1);
	UnityGUI::HelpBox("Changes apply the next time Play starts.", false, 1);
	if (changed)
		SaveProjectSettings();
}

// ==================================================================== 등록
void InputActionsEditorWindow::RegisterEditor()
{
	PlayerInput::OpenEditor = [](const std::string& path) { InputActionsEditorWindow::Open(path); };
	EditorExtensions::AssetType t;
	t.Owner = "core";
	t.Extension = ".inputactions";
	t.Icon = "input_actions";
	t.CreateMenu = "Input Actions";
	t.DefaultName = "New Controls";
	t.Create = [](const std::string& path) {
		std::string name = wstring_to_string(fs::path(string_to_wstring(path)).stem().wstring());
		DefaultAsset(name).Save(path);
	};
	t.Open = [](const std::string& path) { InputActionsEditorWindow::Open(path); };
	t.Inspector = [](const std::string& path) {
		Asset a;
		std::string err;
		if (!a.Load(path, &err))
		{
			UnityGUI::HelpBox(err.c_str(), true);
			return;
		}
		int actions = 0, bindings = 0;
		for (const Map& m : a.Maps) { actions += (int)m.Actions.size(); bindings += (int)m.Bindings.size(); }
		char info[128];
		snprintf(info, sizeof(info), "%d maps, %d actions, %d bindings, %d control schemes", (int)a.Maps.size(), actions, bindings, (int)a.Schemes.size());
		UnityGUI::ValueLabel("Input Actions", info);
		if (UnityGUI::CenterButton("Edit Asset"))
			InputActionsEditorWindow::Open(path);
		UnityGUI::Spacing(6);
		// Unity 의 Import Settings: Generate C# Class
		ImportSettings imp = LoadImportSettings(path);
		bool changed = UnityGUI::Toggle("Generate C# Class", &imp.GenerateWrapperCode);
		if (imp.GenerateWrapperCode)
		{
			changed |= UnityGUI::TextField("C# Class File", &imp.WrapperCodePath, 1);
			changed |= UnityGUI::TextField("C# Class Name", &imp.WrapperClassName, 1);
			changed |= UnityGUI::TextField("C# Class Namespace", &imp.WrapperCodeNamespace, 1);
			UnityGUI::HelpBox("Empty file = next to the asset (<name>.cs), empty class name = the asset name. Regenerated when the asset is saved: new Controls().Player.Move", false, 1);
		}
		if (changed)
			SaveImportSettings(path, imp);
		if (imp.GenerateWrapperCode && UnityGUI::CenterButton("Apply"))
		{
			std::string out, e;
			if (GenerateCSharp(path, &out, &e)) Debug::Log("Input Actions: generated " + out);
			else Debug::LogWarning("Input Actions: " + e);
		}
	};
	EditorExtensions::RegisterAssetType(t);
}

// CLI 는 플레이어에도 (검사 · 자동화)
void InputActionsEditorWindow::RegisterCli()
{
	CliServer::Register("inputactions", "Input Actions assets {op: help | list | create | info | add-map | add-action | add-binding | add-composite | set | set-binding | remove | add-scheme | generate | window | project-wide | settings} (nova inputactions help)",
		[](const json& a, json& r, std::string& e) { return InputActionsCli(a, r, e); });
	CliServer::Register("input", "Input System in Play {op: help | key | mouse | gamepad | release | state | info | action | rebind} (nova input help)",
		[](const json& a, json& r, std::string& e) { return InputCli(a, r, e); });
}
