#include "pch.h"
#include "InputActionsAsset.h"
#include "Debug.h"
#include <filesystem>
#include <fstream>
#include <random>
#include <sstream>
#include <cctype>
#include <deque>
#include <map>
#include <chrono>

namespace fs = std::filesystem;
using json = nlohmann::json;
using ojson = nlohmann::ordered_json;

namespace
{
	std::string Lower(std::string s)
	{
		for (char& c : s) c = (char)std::tolower((unsigned char)c);
		return s;
	}

	bool IEquals(const std::string& a, const std::string& b) { return Lower(a) == Lower(b); }

	std::string Str(const json& j, const char* key)
	{
		if (!j.is_object() || !j.contains(key)) return std::string();
		const json& v = j[key];
		if (v.is_string()) return v.get<std::string>();
		if (v.is_number()) return v.dump();
		return std::string();
	}

	bool Bool(const json& j, const char* key)
	{
		if (!j.is_object() || !j.contains(key)) return false;
		const json& v = j[key];
		if (v.is_boolean()) return v.get<bool>();
		if (v.is_number()) return v.get<double>() != 0.0;
		if (v.is_string()) return IEquals(v.get<std::string>(), "true");
		return false;
	}

	std::string TypeName(const std::string& t)
	{
		if (IEquals(t, "Button") || t == "1") return "Button";
		if (IEquals(t, "PassThrough") || IEquals(t, "Pass Through") || t == "2") return "PassThrough";
		return "Value";
	}

	// "leftStick" → "Left Stick"
	std::string Humanize(const std::string& name)
	{
		std::string out;
		for (size_t i = 0; i < name.size(); ++i)
		{
			const char c = name[i];
			if (i == 0) { out += (char)std::toupper((unsigned char)c); continue; }
			if (std::isupper((unsigned char)c) && !std::isupper((unsigned char)name[i - 1])) out += ' ';
			else if (std::isdigit((unsigned char)c) && !std::isdigit((unsigned char)name[i - 1])) out += ' ';
			out += c;
		}
		return out;
	}

	struct KeyRow { const char* Name; const char* Display; int Vk; };
	const std::vector<KeyRow>& KeyTable()
	{
		static std::vector<KeyRow> t;
		if (!t.empty()) return t;
		static std::deque<std::string> storage;   // 글자 키 이름 (a ~ z, 0 ~ 9 …) — deque 라 늘어나도 c_str 이 그대로
		auto add = [&](const char* n, const char* d, int vk) { t.push_back({ n, d, vk }); };
		add("space", "Space", 0x20); add("enter", "Enter", 0x0D); add("tab", "Tab", 0x09);
		add("backquote", "`", 0xC0); add("quote", "'", 0xDE); add("semicolon", ";", 0xBA); add("comma", ",", 0xBC);
		add("period", ".", 0xBE); add("slash", "/", 0xBF); add("backslash", "\\", 0xDC); add("leftBracket", "[", 0xDB);
		add("rightBracket", "]", 0xDD); add("minus", "-", 0xBD); add("equals", "=", 0xBB);
		for (char c = 'a'; c <= 'z'; ++c)
		{
			storage.push_back(std::string(1, c));
			storage.push_back(std::string(1, (char)std::toupper((unsigned char)c)));
			t.push_back({ storage[storage.size() - 2].c_str(), storage.back().c_str(), 'A' + (c - 'a') });
		}
		for (int n = 1; n <= 10; ++n)
		{
			const int d = n % 10;
			storage.push_back(std::to_string(d));
			t.push_back({ storage.back().c_str(), storage.back().c_str(), '0' + d });
		}
		add("leftShift", "Left Shift", 0xA0); add("rightShift", "Right Shift", 0xA1);
		add("leftAlt", "Left Alt", 0xA4); add("rightAlt", "Right Alt", 0xA5);
		add("leftCtrl", "Left Control", 0xA2); add("rightCtrl", "Right Control", 0xA3);
		add("leftMeta", "Left System", 0x5B); add("rightMeta", "Right System", 0x5C); add("contextMenu", "Context Menu", 0x5D);
		add("escape", "Escape", 0x1B); add("leftArrow", "Left Arrow", 0x25); add("rightArrow", "Right Arrow", 0x27);
		add("upArrow", "Up Arrow", 0x26); add("downArrow", "Down Arrow", 0x28); add("backspace", "Backspace", 0x08);
		add("pageDown", "Page Down", 0x22); add("pageUp", "Page Up", 0x21); add("home", "Home", 0x24); add("end", "End", 0x23);
		add("insert", "Insert", 0x2D); add("delete", "Delete", 0x2E); add("capsLock", "Caps Lock", 0x14); add("numLock", "Num Lock", 0x90);
		add("printScreen", "Print Screen", 0x2C); add("scrollLock", "Scroll Lock", 0x91); add("pause", "Pause/Break", 0x13);
		add("numpadEnter", "Numpad Enter", 0); add("numpadDivide", "Numpad /", 0x6F); add("numpadMultiply", "Numpad *", 0x6A);
		add("numpadPlus", "Numpad +", 0x6B); add("numpadMinus", "Numpad -", 0x6D); add("numpadPeriod", "Numpad .", 0x6E); add("numpadEquals", "Numpad =", 0x92);
		for (int n = 0; n <= 9; ++n)
		{
			storage.push_back("numpad" + std::to_string(n));
			storage.push_back("Numpad " + std::to_string(n));
			t.push_back({ storage[storage.size() - 2].c_str(), storage.back().c_str(), 0x60 + n });
		}
		for (int n = 1; n <= 12; ++n)
		{
			storage.push_back("f" + std::to_string(n));
			storage.push_back("F" + std::to_string(n));
			t.push_back({ storage[storage.size() - 2].c_str(), storage.back().c_str(), 0x70 + n - 1 });
		}
		add("OEM1", "OEM1", 0xE2);
		return t;
	}

	std::vector<InputActions::LayoutInfo> BuildLayouts()
	{
		using L = InputActions::LayoutInfo;
		std::vector<L> out;
		auto stick = [](L& l, const std::string& name, const std::string& display, const std::string& shortName, const std::string& layout) {
			l.Controls.push_back({ name, display, shortName, layout });
			for (const char* part : { "up", "down", "left", "right" })
				l.Controls.push_back({ name + "/" + part, display + "/" + Humanize(part), "", "Button" });
			l.Controls.push_back({ name + "/x", display + "/X", "", "Axis" });
			l.Controls.push_back({ name + "/y", display + "/Y", "", "Axis" });
		};

		L kb{ "Keyboard", "Keyboard", "", {} };
		kb.Controls.push_back({ "anyKey", "Any Key", "", "AnyKey" });
		for (const KeyRow& k : KeyTable())
			kb.Controls.push_back({ k.Name, k.Display, "", "Key" });
		kb.Controls.push_back({ "shift", "Shift", "", "Button" });
		kb.Controls.push_back({ "ctrl", "Control", "", "Button" });
		kb.Controls.push_back({ "alt", "Alt", "", "Button" });
		out.push_back(kb);

		L ptr{ "Pointer", "Pointer", "", {} };
		ptr.Controls.push_back({ "position", "Position", "", "Vector2" });
		ptr.Controls.push_back({ "position/x", "Position/X", "", "Axis" });
		ptr.Controls.push_back({ "position/y", "Position/Y", "", "Axis" });
		stick(ptr, "delta", "Delta", "", "Delta");
		ptr.Controls.push_back({ "press", "Press", "", "Button" });
		ptr.Controls.push_back({ "pressure", "Pressure", "", "Axis" });
		out.push_back(ptr);

		L mouse{ "Mouse", "Mouse", "Pointer", {} };
		mouse.Controls = ptr.Controls;
		stick(mouse, "scroll", "Scroll", "", "Delta");
		mouse.Controls.push_back({ "leftButton", "Left Button", "LMB", "Button" });
		mouse.Controls.push_back({ "rightButton", "Right Button", "RMB", "Button" });
		mouse.Controls.push_back({ "middleButton", "Middle Button", "MMB", "Button" });
		mouse.Controls.push_back({ "forwardButton", "Forward", "", "Button" });
		mouse.Controls.push_back({ "backButton", "Back", "", "Button" });
		mouse.Controls.push_back({ "clickCount", "Click Count", "", "Integer" });
		out.push_back(mouse);

		L pad{ "Gamepad", "Gamepad", "", {} };
		pad.Controls.push_back({ "buttonSouth", "Button South", "A", "Button" });
		pad.Controls.push_back({ "buttonEast", "Button East", "B", "Button" });
		pad.Controls.push_back({ "buttonWest", "Button West", "X", "Button" });
		pad.Controls.push_back({ "buttonNorth", "Button North", "Y", "Button" });
		pad.Controls.push_back({ "leftShoulder", "Left Shoulder", "LB", "Button" });
		pad.Controls.push_back({ "rightShoulder", "Right Shoulder", "RB", "Button" });
		pad.Controls.push_back({ "leftTrigger", "Left Trigger", "LT", "Button" });
		pad.Controls.push_back({ "rightTrigger", "Right Trigger", "RT", "Button" });
		pad.Controls.push_back({ "start", "Start", "", "Button" });
		pad.Controls.push_back({ "select", "Select", "", "Button" });
		pad.Controls.push_back({ "leftStickPress", "Left Stick Press", "LS", "Button" });
		pad.Controls.push_back({ "rightStickPress", "Right Stick Press", "RS", "Button" });
		stick(pad, "leftStick", "Left Stick", "LS", "Stick");
		stick(pad, "rightStick", "Right Stick", "RS", "Stick");
		stick(pad, "dpad", "D-Pad", "", "Dpad");
		out.push_back(pad);

		L xbox{ "XInputController", "Xbox Controller", "Gamepad", pad.Controls };
		for (auto& c : xbox.Controls)
		{
			if (c.Path == "buttonSouth") c.Display = "A";
			else if (c.Path == "buttonEast") c.Display = "B";
			else if (c.Path == "buttonWest") c.Display = "X";
			else if (c.Path == "buttonNorth") c.Display = "Y";
			else if (c.Path == "leftShoulder") c.Display = "Left Bumper";
			else if (c.Path == "rightShoulder") c.Display = "Right Bumper";
			else if (c.Path == "start") c.Display = "Menu";
			else if (c.Path == "select") c.Display = "View";
		}
		out.push_back(xbox);

		L touch{ "Touchscreen", "Touchscreen", "Pointer", {} };
		const char* tparts[][3] = { { "position", "Position", "Vector2" }, { "delta", "Delta", "Delta" }, { "press", "Press", "TouchPress" }, { "tap", "Tap", "Button" },
			{ "phase", "Phase", "TouchPhase" }, { "startPosition", "Start Position", "Vector2" }, { "tapCount", "Tap Count", "Integer" }, { "touchId", "Touch ID", "Integer" }, { "pressure", "Pressure", "Axis" } };
		touch.Controls.push_back({ "primaryTouch", "Primary Touch", "", "Touch" });
		for (auto& p : tparts)
			touch.Controls.push_back({ std::string("primaryTouch/") + p[0], std::string("Primary Touch/") + p[1], "", p[2] });
		touch.Controls.push_back({ "position", "Position", "", "Vector2" });
		touch.Controls.push_back({ "delta", "Delta", "", "Delta" });
		touch.Controls.push_back({ "press", "Press", "", "Button" });
		for (int i = 0; i < 10; ++i)
			touch.Controls.push_back({ "touch" + std::to_string(i), "Touch #" + std::to_string(i), "", "Touch" });
		for (auto& p : tparts)
			touch.Controls.push_back({ std::string("touch*/") + p[0], std::string("Touch*/") + p[1], "", p[2] });
		out.push_back(touch);
		return out;
	}

	std::string Indent(int n) { return std::string((size_t)n * 4, ' '); }

	// C# 식별자 (Unity 의 CSharpCodeHelpers.MakeIdentifier)
	std::string Identifier(const std::string& s)
	{
		std::string out;
		for (char c : s)
			if (std::isalnum((unsigned char)c) || c == '_') out += c;
		if (out.empty() || std::isdigit((unsigned char)out[0])) out = "_" + out;
		return out;
	}

	std::string TypeIdent(const std::string& s)
	{
		std::string id = Identifier(s);
		if (!id.empty() && id[0] != '_') id[0] = (char)std::toupper((unsigned char)id[0]);
		return id;
	}

	std::string CsString(const std::string& s)
	{
		std::string out = "\"";
		for (char c : s)
		{
			if (c == '"' || c == '\\') out += '\\';
			out += c;
		}
		return out + "\"";
	}
}

namespace InputActions
{
	// ---------------------------------------------------------------- Map
	int Map::FindAction(const std::string& name) const
	{
		for (size_t i = 0; i < Actions.size(); ++i)
			if (IEquals(Actions[i].Name, name) || (!Actions[i].Id.empty() && IEquals(Actions[i].Id, name)))
				return (int)i;
		return -1;
	}

	static bool Targets(const Binding& b, const std::string& action, const std::string& actionId)
	{
		std::string a = b.Action;
		if (a.size() > 2 && a.front() == '{' && a.back() == '}') a = a.substr(1, a.size() - 2);
		const size_t slash = a.find('/');
		if (slash != std::string::npos) a = a.substr(slash + 1);
		return IEquals(a, action) || (!actionId.empty() && IEquals(a, actionId));
	}

	std::vector<int> Map::BindingsOf(const std::string& action) const
	{
		std::vector<int> out;
		const int ai = FindAction(action);
		const std::string id = ai >= 0 ? Actions[(size_t)ai].Id : std::string();
		const std::string name = ai >= 0 ? Actions[(size_t)ai].Name : action;
		for (int i = 0; i < (int)Bindings.size(); ++i)
		{
			if (Bindings[(size_t)i].IsPartOfComposite)
				continue;
			if (!Targets(Bindings[(size_t)i], name, id))
				continue;
			out.push_back(i);
			if (Bindings[(size_t)i].IsComposite)
				for (int p : PartsOf(i)) out.push_back(p);
		}
		return out;
	}

	std::vector<int> Map::PartsOf(int compositeIndex) const
	{
		std::vector<int> out;
		for (int i = compositeIndex + 1; i < (int)Bindings.size() && Bindings[(size_t)i].IsPartOfComposite; ++i)
			out.push_back(i);
		return out;
	}

	int Map::EndOfComposite(int compositeIndex) const
	{
		int i = compositeIndex + 1;
		while (i < (int)Bindings.size() && Bindings[(size_t)i].IsPartOfComposite) ++i;
		return i;
	}

	void Map::RenameAction(const std::string& oldName, const std::string& newName)
	{
		const int ai = FindAction(oldName);
		if (ai < 0) return;
		const std::string id = Actions[(size_t)ai].Id;
		for (Binding& b : Bindings)
			if (Targets(b, Actions[(size_t)ai].Name, id))
				b.Action = newName;
		Actions[(size_t)ai].Name = newName;
	}

	void Map::RemoveAction(const std::string& name)
	{
		const int ai = FindAction(name);
		if (ai < 0) return;
		const std::vector<int> idx = BindingsOf(Actions[(size_t)ai].Name);
		for (auto it = idx.rbegin(); it != idx.rend(); ++it)
			Bindings.erase(Bindings.begin() + *it);
		Actions.erase(Actions.begin() + ai);
	}

	void Map::RemoveBinding(int index)
	{
		if (index < 0 || index >= (int)Bindings.size()) return;
		const int end = Bindings[(size_t)index].IsComposite ? EndOfComposite(index) : index + 1;
		Bindings.erase(Bindings.begin() + index, Bindings.begin() + end);
	}

	int Map::InsertPointFor(const std::string& action) const
	{
		const std::vector<int> idx = BindingsOf(action);
		return idx.empty() ? (int)Bindings.size() : idx.back() + 1;
	}

	// ---------------------------------------------------------------- Asset
	ojson Asset::ToJson() const
	{
		ojson j;
		j["name"] = Name;
		ojson maps = ojson::array();
		for (const Map& m : Maps)
		{
			ojson mj;
			mj["name"] = m.Name;
			mj["id"] = m.Id;
			ojson acts = ojson::array();
			for (const Action& a : m.Actions)
			{
				ojson aj;
				aj["name"] = a.Name;
				aj["type"] = a.Type;
				aj["id"] = a.Id;
				aj["expectedControlType"] = a.ExpectedControlType;
				aj["processors"] = a.Processors;
				aj["interactions"] = a.Interactions;
				aj["initialStateCheck"] = a.InitialStateCheck;
				acts.push_back(aj);
			}
			mj["actions"] = acts;
			ojson binds = ojson::array();
			for (const Binding& b : m.Bindings)
			{
				ojson bj;
				bj["name"] = b.Name;
				bj["id"] = b.Id;
				bj["path"] = b.Path;
				bj["interactions"] = b.Interactions;
				bj["processors"] = b.Processors;
				bj["groups"] = b.Groups;
				bj["action"] = b.Action;
				bj["isComposite"] = b.IsComposite;
				bj["isPartOfComposite"] = b.IsPartOfComposite;
				binds.push_back(bj);
			}
			mj["bindings"] = binds;
			maps.push_back(mj);
		}
		j["maps"] = maps;
		ojson schemes = ojson::array();
		for (const ControlScheme& s : Schemes)
		{
			ojson sj;
			sj["name"] = s.Name;
			sj["bindingGroup"] = s.BindingGroup;
			ojson devs = ojson::array();
			for (const DeviceRequirement& d : s.Devices)
			{
				ojson dj;
				dj["devicePath"] = d.DevicePath;
				dj["isOptional"] = d.IsOptional;
				dj["isOR"] = d.IsOR;
				devs.push_back(dj);
			}
			sj["devices"] = devs;
			schemes.push_back(sj);
		}
		j["controlSchemes"] = schemes;
		return j;
	}

	void Asset::FromJson(const json& j)
	{
		Name = Str(j, "name");
		Maps.clear();
		Schemes.clear();
		if (j.contains("maps") && j["maps"].is_array())
			for (const json& mj : j["maps"])
			{
				Map m;
				m.Name = Str(mj, "name");
				m.Id = Str(mj, "id");
				if (m.Id.empty()) m.Id = NewGuid();
				if (mj.contains("actions") && mj["actions"].is_array())
					for (const json& aj : mj["actions"])
					{
						Action a;
						a.Name = Str(aj, "name");
						a.Type = TypeName(Str(aj, "type"));
						a.Id = Str(aj, "id");
						if (a.Id.empty()) a.Id = NewGuid();
						a.ExpectedControlType = Str(aj, "expectedControlType");
						a.Processors = Str(aj, "processors");
						a.Interactions = Str(aj, "interactions");
						a.InitialStateCheck = Bool(aj, "initialStateCheck");
						m.Actions.push_back(a);
					}
				if (mj.contains("bindings") && mj["bindings"].is_array())
					for (const json& bj : mj["bindings"])
					{
						Binding b;
						b.Name = Str(bj, "name");
						b.Id = Str(bj, "id");
						if (b.Id.empty()) b.Id = NewGuid();
						b.Path = Str(bj, "path");
						b.Interactions = Str(bj, "interactions");
						b.Processors = Str(bj, "processors");
						b.Groups = Str(bj, "groups");
						b.Action = Str(bj, "action");
						b.IsComposite = Bool(bj, "isComposite");
						b.IsPartOfComposite = Bool(bj, "isPartOfComposite");
						m.Bindings.push_back(b);
					}
				Maps.push_back(m);
			}
		if (j.contains("controlSchemes") && j["controlSchemes"].is_array())
			for (const json& sj : j["controlSchemes"])
			{
				ControlScheme s;
				s.Name = Str(sj, "name");
				s.BindingGroup = Str(sj, "bindingGroup");
				if (s.BindingGroup.empty()) s.BindingGroup = s.Name;
				if (sj.contains("devices") && sj["devices"].is_array())
					for (const json& dj : sj["devices"])
						s.Devices.push_back({ Str(dj, "devicePath"), Bool(dj, "isOptional"), Bool(dj, "isOR") });
				Schemes.push_back(s);
			}
	}

	bool Asset::Load(const std::string& relPath, std::string* error)
	{
		std::ifstream in(fs::path(FullPath(relPath)), std::ios::binary);
		if (!in)
		{
			if (error) *error = "cannot open " + relPath;
			return false;
		}
		std::stringstream ss;
		ss << in.rdbuf();
		std::string text = ss.str();
		if (text.size() >= 3 && (unsigned char)text[0] == 0xEF && (unsigned char)text[1] == 0xBB && (unsigned char)text[2] == 0xBF)
			text = text.substr(3);
		const json j = json::parse(text, nullptr, false, true);
		if (j.is_discarded() || !j.is_object())
		{
			if (error) *error = "not an Input Actions JSON: " + relPath;
			return false;
		}
		FromJson(j);
		if (Name.empty())
			Name = wstring_to_string(fs::path(FullPath(relPath)).stem().wstring());
		return true;
	}

	bool Asset::Save(const std::string& relPath, std::string* error) const
	{
		const fs::path p(FullPath(relPath));
		std::error_code ec;
		fs::create_directories(p.parent_path(), ec);
		std::ofstream out(p, std::ios::binary | std::ios::trunc);
		if (!out)
		{
			if (error) *error = "cannot write " + relPath;
			return false;
		}
		out << ToJson().dump(4);
		return true;
	}

	int Asset::FindMap(const std::string& name) const
	{
		for (size_t i = 0; i < Maps.size(); ++i)
			if (IEquals(Maps[i].Name, name) || IEquals(Maps[i].Id, name))
				return (int)i;
		return -1;
	}

	int Asset::FindScheme(const std::string& name) const
	{
		for (size_t i = 0; i < Schemes.size(); ++i)
			if (IEquals(Schemes[i].Name, name))
				return (int)i;
		return -1;
	}

	const Action* Asset::FindAction(const std::string& nameOrId, const Map** mapOut) const
	{
		const size_t slash = nameOrId.find('/');
		for (const Map& m : Maps)
		{
			if (slash != std::string::npos && !IEquals(m.Name, nameOrId.substr(0, slash)))
				continue;
			const int ai = m.FindAction(slash != std::string::npos ? nameOrId.substr(slash + 1) : nameOrId);
			if (ai >= 0)
			{
				if (mapOut) *mapOut = &m;
				return &m.Actions[(size_t)ai];
			}
		}
		return nullptr;
	}

	Action* Asset::FindAction(const std::string& nameOrId, Map** mapOut)
	{
		const Map* m = nullptr;
		const Action* a = static_cast<const Asset*>(this)->FindAction(nameOrId, &m);
		if (mapOut) *mapOut = const_cast<Map*>(m);
		return const_cast<Action*>(a);
	}

	std::string NewGuid()
	{
		static std::mt19937_64 rng([] { std::random_device rd; return ((uint64_t)rd() << 32) ^ rd() ^ (uint64_t)std::chrono::high_resolution_clock::now().time_since_epoch().count(); }());
		const uint64_t a = rng(), b = rng();
		char buf[40];
		snprintf(buf, sizeof(buf), "%08x-%04x-4%03x-%04x-%012llx", (unsigned)(a >> 32), (unsigned)((a >> 16) & 0xffff), (unsigned)(a & 0xfff),
			(unsigned)(((b >> 48) & 0x3fff) | 0x8000), (unsigned long long)(b & 0xffffffffffffull));
		return buf;
	}

	Asset DefaultAsset(const std::string& name)
	{
		Asset a;
		a.Name = name;
		auto action = [](Map& m, const char* n, const char* type, const char* control, const char* interactions = "", bool initial = false) {
			Action x;
			x.Name = n;
			x.Type = type;
			x.Id = NewGuid();
			x.ExpectedControlType = control;
			x.Interactions = interactions;
			x.InitialStateCheck = initial || std::string(type) == "Value";
			m.Actions.push_back(x);
		};
		auto bind = [](Map& m, const char* act, const char* path, const char* groups, const char* name = "", bool composite = false, bool part = false) {
			Binding b;
			b.Name = name;
			b.Id = NewGuid();
			b.Path = path;
			b.Groups = groups;
			b.Action = act;
			b.IsComposite = composite;
			b.IsPartOfComposite = part;
			m.Bindings.push_back(b);
		};
		auto wasd = [&](Map& m, const char* act, const char* name, const char* groups) {
			bind(m, act, "2DVector", "", name, true);
			bind(m, act, "<Keyboard>/w", groups, "up", false, true);
			bind(m, act, "<Keyboard>/upArrow", groups, "up", false, true);
			bind(m, act, "<Keyboard>/s", groups, "down", false, true);
			bind(m, act, "<Keyboard>/downArrow", groups, "down", false, true);
			bind(m, act, "<Keyboard>/a", groups, "left", false, true);
			bind(m, act, "<Keyboard>/leftArrow", groups, "left", false, true);
			bind(m, act, "<Keyboard>/d", groups, "right", false, true);
			bind(m, act, "<Keyboard>/rightArrow", groups, "right", false, true);
		};

		Map player{ "Player", NewGuid(), {}, {} };
		action(player, "Move", "Value", "Vector2");
		action(player, "Look", "Value", "Vector2");
		action(player, "Attack", "Button", "Button");
		action(player, "Interact", "Button", "Button", "Hold");
		action(player, "Crouch", "Button", "Button");
		action(player, "Jump", "Button", "Button");
		action(player, "Previous", "Button", "Button");
		action(player, "Next", "Button", "Button");
		action(player, "Sprint", "Button", "Button");
		bind(player, "Move", "<Gamepad>/leftStick", ";Gamepad");
		wasd(player, "Move", "WASD", ";Keyboard&Mouse");
		bind(player, "Look", "<Gamepad>/rightStick", ";Gamepad");
		bind(player, "Look", "<Pointer>/delta", ";Keyboard&Mouse;Touch");
		bind(player, "Attack", "<Gamepad>/buttonWest", ";Gamepad");
		bind(player, "Attack", "<Mouse>/leftButton", ";Keyboard&Mouse");
		bind(player, "Attack", "<Touchscreen>/primaryTouch/tap", ";Touch");
		bind(player, "Attack", "<Keyboard>/enter", "Keyboard&Mouse");
		bind(player, "Interact", "<Keyboard>/e", "Keyboard&Mouse");
		bind(player, "Interact", "<Gamepad>/buttonNorth", "Gamepad");
		bind(player, "Crouch", "<Keyboard>/c", "Keyboard&Mouse");
		bind(player, "Crouch", "<Gamepad>/buttonEast", "Gamepad");
		bind(player, "Jump", "<Keyboard>/space", "Keyboard&Mouse");
		bind(player, "Jump", "<Gamepad>/buttonSouth", "Gamepad");
		bind(player, "Previous", "<Keyboard>/1", "Keyboard&Mouse");
		bind(player, "Previous", "<Gamepad>/dpad/left", "Gamepad");
		bind(player, "Next", "<Keyboard>/2", "Keyboard&Mouse");
		bind(player, "Next", "<Gamepad>/dpad/right", "Gamepad");
		bind(player, "Sprint", "<Keyboard>/leftShift", "Keyboard&Mouse");
		bind(player, "Sprint", "<Gamepad>/leftStickPress", "Gamepad");
		a.Maps.push_back(player);

		Map ui{ "UI", NewGuid(), {}, {} };
		action(ui, "Navigate", "PassThrough", "Vector2");
		action(ui, "Submit", "Button", "Button");
		action(ui, "Cancel", "Button", "Button");
		action(ui, "Point", "PassThrough", "Vector2", "", true);
		action(ui, "Click", "PassThrough", "Button", "", true);
		action(ui, "RightClick", "PassThrough", "Button");
		action(ui, "MiddleClick", "PassThrough", "Button");
		action(ui, "ScrollWheel", "PassThrough", "Vector2");
		bind(ui, "Navigate", "2DVector", "", "Gamepad", true);
		bind(ui, "Navigate", "<Gamepad>/leftStick/up", ";Gamepad", "up", false, true);
		bind(ui, "Navigate", "<Gamepad>/rightStick/up", ";Gamepad", "up", false, true);
		bind(ui, "Navigate", "<Gamepad>/leftStick/down", ";Gamepad", "down", false, true);
		bind(ui, "Navigate", "<Gamepad>/rightStick/down", ";Gamepad", "down", false, true);
		bind(ui, "Navigate", "<Gamepad>/leftStick/left", ";Gamepad", "left", false, true);
		bind(ui, "Navigate", "<Gamepad>/rightStick/left", ";Gamepad", "left", false, true);
		bind(ui, "Navigate", "<Gamepad>/leftStick/right", ";Gamepad", "right", false, true);
		bind(ui, "Navigate", "<Gamepad>/rightStick/right", ";Gamepad", "right", false, true);
		bind(ui, "Navigate", "<Gamepad>/dpad", ";Gamepad");
		wasd(ui, "Navigate", "Keyboard", "Keyboard&Mouse");
		bind(ui, "Submit", "*/{Submit}", "Keyboard&Mouse;Gamepad;Touch");
		bind(ui, "Cancel", "*/{Cancel}", "Keyboard&Mouse;Gamepad;Touch");
		bind(ui, "Point", "<Mouse>/position", "Keyboard&Mouse");
		bind(ui, "Point", "<Touchscreen>/touch*/position", "Touch");
		bind(ui, "Click", "<Mouse>/leftButton", ";Keyboard&Mouse");
		bind(ui, "Click", "<Touchscreen>/touch*/press", "Touch");
		bind(ui, "ScrollWheel", "<Mouse>/scroll", ";Keyboard&Mouse");
		bind(ui, "RightClick", "<Mouse>/rightButton", "Keyboard&Mouse");
		bind(ui, "MiddleClick", "<Mouse>/middleButton", "Keyboard&Mouse");
		a.Maps.push_back(ui);

		a.Schemes.push_back({ "Keyboard&Mouse", "Keyboard&Mouse", { { "<Keyboard>", false, false }, { "<Mouse>", false, false } } });
		a.Schemes.push_back({ "Gamepad", "Gamepad", { { "<Gamepad>", false, false } } });
		a.Schemes.push_back({ "Touch", "Touch", { { "<Touchscreen>", false, false } } });
		return a;
	}

	std::wstring FullPath(const std::string& relPath)
	{
		std::wstring w = string_to_wstring(relPath);
		std::replace(w.begin(), w.end(), L'/', L'\\');
		return PathManager::GetI()->GetMovePathW(w);
	}

	bool IsInputActionsPath(const std::string& path)
	{
		const std::string l = Lower(path);
		return l.size() > 13 && l.compare(l.size() - 13, 13, ".inputactions") == 0;
	}

	std::vector<std::string> FindAll()
	{
		std::vector<std::string> out;
		const std::wstring root = PathManager::GetI()->GetMovePathW(L"Assets\\");
		std::error_code ec;
		if (!fs::exists(root, ec))
			return out;
		for (const auto& e : fs::recursive_directory_iterator(root, ec))
			if (e.is_regular_file(ec) && _wcsicmp(e.path().extension().c_str(), L".inputactions") == 0)
				out.push_back(wstring_to_string(L"Assets\\" + fs::relative(e.path(), root, ec).wstring()));
		std::sort(out.begin(), out.end());
		return out;
	}

	// ---------------------------------------------------------------- 레이아웃 · 보여 줄 이름
	const std::vector<LayoutInfo>& Layouts()
	{
		static const std::vector<LayoutInfo> s = BuildLayouts();
		return s;
	}

	const LayoutInfo* FindLayout(const std::string& name)
	{
		for (const LayoutInfo& l : Layouts())
			if (IEquals(l.Name, name)) return &l;
		if (IEquals(name, "XInputControllerWindows")) return FindLayout("XInputController");
		return nullptr;
	}

	const ControlInfo* FindControl(const std::string& layout, const std::string& path)
	{
		const LayoutInfo* l = FindLayout(layout);
		if (!l) return nullptr;
		for (const ControlInfo& c : l->Controls)
			if (IEquals(c.Path, path)) return &c;
		// 별칭 (a · cross …)
		static const std::pair<const char*, const char*> kAlias[] = { { "a", "buttonSouth" }, { "cross", "buttonSouth" }, { "b", "buttonEast" }, { "circle", "buttonEast" },
			{ "x", "buttonWest" }, { "square", "buttonWest" }, { "y", "buttonNorth" }, { "triangle", "buttonNorth" } };
		if (l->Name == "Gamepad" || l->Name == "XInputController")
			for (auto& al : kAlias)
				if (IEquals(path, al.first)) return FindControl(layout, al.second);
		return nullptr;
	}

	const std::vector<CompositeInfo>& Composites()
	{
		static const std::vector<CompositeInfo> s = {
			{ "1DAxis", "1D Axis", { "negative", "positive" }, "Axis" },
			{ "2DVector", "2D Vector", { "up", "down", "left", "right" }, "Vector2" },
			{ "3DVector", "3D Vector", { "up", "down", "left", "right", "forward", "backward" }, "Vector3" },
			{ "ButtonWithOneModifier", "Button With One Modifier", { "modifier", "button" }, "Button" },
			{ "ButtonWithTwoModifiers", "Button With Two Modifiers", { "modifier1", "modifier2", "button" }, "Button" },
			{ "OneModifier", "One Modifier", { "modifier", "binding" }, "" },
			{ "TwoModifiers", "Two Modifiers", { "modifier1", "modifier2", "binding" }, "" },
		};
		return s;
	}

	const CompositeInfo* FindComposite(const std::string& nameOrPath)
	{
		std::string n = nameOrPath.substr(0, nameOrPath.find('('));
		if (IEquals(n, "Dpad")) n = "2DVector";
		if (IEquals(n, "Axis")) n = "1DAxis";
		for (const CompositeInfo& c : Composites())
			if (IEquals(c.Name, n)) return &c;
		return nullptr;
	}

	std::string DisplayPath(const std::string& path, bool withDevice)
	{
		if (path.empty())
			return "<No Binding>";
		if (path.find('<') == std::string::npos && path.find('/') == std::string::npos && path.find('{') == std::string::npos)
		{
			const CompositeInfo* c = FindComposite(path);
			return c ? c->Display : path;
		}
		std::string p = path;
		if (!p.empty() && p[0] == '/') p = p.substr(1);
		const size_t slash = p.find('/');
		std::string dev = slash == std::string::npos ? p : p.substr(0, slash);
		const std::string rest = slash == std::string::npos ? std::string() : p.substr(slash + 1);
		std::string layout = dev;
		if (dev.size() > 2 && dev.front() == '<')
			layout = dev.substr(1, dev.find('>') - 1);
		std::string devDisplay = layout == "*" ? "Any" : layout;
		if (const LayoutInfo* l = FindLayout(layout))
			devDisplay = l->Display;
		std::string ctl;
		if (const ControlInfo* c = FindControl(layout, rest))
			ctl = c->Display;
		else
		{
			// {Submit} · touch*/position · 모르는 레이아웃
			std::string part;
			std::stringstream ss(rest);
			while (std::getline(ss, part, '/'))
			{
				if (!ctl.empty()) ctl += "/";
				if (part.size() > 2 && part.front() == '{') ctl += part.substr(1, part.size() - 2);
				else ctl += Humanize(part);
			}
		}
		if (rest.empty())
			return devDisplay;
		return withDevice ? ctl + " [" + devDisplay + "]" : ctl;
	}

	std::string DisplayBinding(const Map& map, int index)
	{
		if (index < 0 || index >= (int)map.Bindings.size())
			return std::string();
		const Binding& b = map.Bindings[(size_t)index];
		if (!b.IsComposite)
			return DisplayPath(b.Path, false);
		std::map<std::string, std::string> parts;
		for (int p : map.PartsOf(index))
		{
			const Binding& pb = map.Bindings[(size_t)p];
			std::string& s = parts[Lower(pb.Name)];
			const std::string d = DisplayPath(pb.Path, false);
			if (s.find(d) == std::string::npos) s += (s.empty() ? "" : "|") + d;
		}
		const CompositeInfo* c = FindComposite(b.Path);
		std::string format = "{up}/{left}/{down}/{right}";
		if (c)
		{
			const std::string n = c->Name;
			if (n == "1DAxis") format = "{negative}/{positive}";
			else if (n == "3DVector") format = "{up}+{down}/{left}+{right}/{forward}+{backward}";
			else if (n == "ButtonWithOneModifier") format = "{modifier}+{button}";
			else if (n == "ButtonWithTwoModifiers") format = "{modifier1}+{modifier2}+{button}";
			else if (n == "OneModifier") format = "{modifier}+{binding}";
			else if (n == "TwoModifiers") format = "{modifier1}+{modifier2}+{binding}";
		}
		std::string out;
		for (size_t i = 0; i < format.size();)
		{
			if (format[i] == '{')
			{
				const size_t end = format.find('}', i);
				out += parts[format.substr(i + 1, end - i - 1)];
				i = end + 1;
			}
			else out += format[i++];
		}
		return out;
	}

	bool ControlTypeMatches(const std::string& expected, const std::string& layout)
	{
		auto kind = [](const std::string& t) -> int {
			const std::string l = Lower(t);
			if (l == "button" || l == "axis" || l == "key" || l == "analog" || l == "digital" || l == "anykey" || l == "touchpress" || l == "discretebutton") return 1;
			if (l == "vector2" || l == "stick" || l == "dpad" || l == "delta") return 2;
			if (l == "vector3") return 3;
			if (l == "integer") return 4;
			if (l == "touch") return 5;
			if (l == "touchphase") return 4;
			return 0;
		};
		const int e = kind(expected);
		return e == 0 || e == kind(layout);
	}

	int KeyVk(const std::string& keyName)
	{
		for (const KeyRow& k : KeyTable())
			if (IEquals(k.Name, keyName)) return k.Vk;
		if (IEquals(keyName, "shift")) return 0xA0;
		if (IEquals(keyName, "ctrl")) return 0xA2;
		if (IEquals(keyName, "alt")) return 0xA4;
		return 0;
	}

	std::string KeyNameFromVk(int vk)
	{
		if (vk == 0x10) vk = 0xA0;
		if (vk == 0x11) vk = 0xA2;
		if (vk == 0x12) vk = 0xA4;
		for (const KeyRow& k : KeyTable())
			if (k.Vk == vk) return k.Name;
		return std::string();
	}

	// ---------------------------------------------------------------- interactions · processors
	const std::vector<NamedInfo>& InteractionTypes()
	{
		static const std::vector<NamedInfo> s = {
			{ "Hold", "Hold", { { "duration", "float", "0" }, { "pressPoint", "float", "0" } }, "" },
			{ "MultiTap", "Multi Tap", { { "tapTime", "float", "0" }, { "tapDelay", "float", "0" }, { "tapCount", "int", "2" }, { "pressPoint", "float", "0" } }, "" },
			{ "Press", "Press", { { "pressPoint", "float", "0" }, { "behavior", "enum:Press Only,Release Only,Press And Release", "0" } }, "" },
			{ "SlowTap", "Slow Tap", { { "duration", "float", "0" }, { "pressPoint", "float", "0" } }, "" },
			{ "Tap", "Tap", { { "duration", "float", "0" }, { "pressPoint", "float", "0" } }, "" },
		};
		return s;
	}

	const std::vector<NamedInfo>& ProcessorTypes()
	{
		static const std::vector<NamedInfo> s = {
			{ "AxisDeadzone", "Axis Deadzone", { { "min", "float", "0" }, { "max", "float", "0" } }, "float" },
			{ "Clamp", "Clamp", { { "min", "float", "0" }, { "max", "float", "0" } }, "float" },
			{ "Invert", "Invert", {}, "float" },
			{ "InvertVector2", "Invert Vector 2", { { "invertX", "bool", "true" }, { "invertY", "bool", "true" } }, "Vector2" },
			{ "InvertVector3", "Invert Vector 3", { { "invertX", "bool", "true" }, { "invertY", "bool", "true" }, { "invertZ", "bool", "true" } }, "Vector3" },
			{ "Normalize", "Normalize", { { "min", "float", "0" }, { "max", "float", "0" }, { "zero", "float", "0" } }, "float" },
			{ "NormalizeVector2", "Normalize Vector 2", {}, "Vector2" },
			{ "NormalizeVector3", "Normalize Vector 3", {}, "Vector3" },
			{ "Scale", "Scale", { { "factor", "float", "1" } }, "float" },
			{ "ScaleVector2", "Scale Vector 2", { { "x", "float", "1" }, { "y", "float", "1" } }, "Vector2" },
			{ "ScaleVector3", "Scale Vector 3", { { "x", "float", "1" }, { "y", "float", "1" }, { "z", "float", "1" } }, "Vector3" },
			{ "StickDeadzone", "Stick Deadzone", { { "min", "float", "0" }, { "max", "float", "0" } }, "Vector2" },
		};
		return s;
	}

	std::vector<NameAndParams> ParseList(const std::string& text)
	{
		std::vector<NameAndParams> out;
		size_t i = 0;
		while (i < text.size())
		{
			while (i < text.size() && (std::isspace((unsigned char)text[i]) || text[i] == ',' || text[i] == ';')) ++i;
			if (i >= text.size()) break;
			const size_t start = i;
			while (i < text.size() && text[i] != '(' && text[i] != ',' && text[i] != ';') ++i;
			NameAndParams n;
			n.Name = text.substr(start, i - start);
			while (!n.Name.empty() && std::isspace((unsigned char)n.Name.back())) n.Name.pop_back();
			if (i < text.size() && text[i] == '(')
			{
				size_t close = text.find(')', i);
				if (close == std::string::npos) close = text.size();
				std::stringstream ss(text.substr(i + 1, close - i - 1));
				std::string part;
				while (std::getline(ss, part, ','))
				{
					const size_t eq = part.find('=');
					auto trim = [](std::string s) { while (!s.empty() && std::isspace((unsigned char)s.front())) s.erase(s.begin()); while (!s.empty() && std::isspace((unsigned char)s.back())) s.pop_back(); return s; };
					if (eq == std::string::npos) { if (!trim(part).empty()) n.Params.push_back({ trim(part), "true" }); }
					else n.Params.push_back({ trim(part.substr(0, eq)), trim(part.substr(eq + 1)) });
				}
				i = close + 1;
			}
			if (!n.Name.empty()) out.push_back(n);
		}
		return out;
	}

	std::string JoinList(const std::vector<NameAndParams>& list)
	{
		std::string out;
		for (const NameAndParams& n : list)
		{
			if (!out.empty()) out += ",";
			out += n.Name;
			if (!n.Params.empty())
			{
				out += "(";
				for (size_t i = 0; i < n.Params.size(); ++i)
					out += (i ? "," : "") + n.Params[i].first + "=" + n.Params[i].second;
				out += ")";
			}
		}
		return out;
	}

	// ---------------------------------------------------------------- Project Settings
	static Settings s_Settings;
	static bool s_SettingsLoaded = false;

	static std::wstring SettingsFile() { return PathManager::GetI()->GetMovePathW(L"ProjectSettings\\InputSystem.json"); }

	void ReloadProjectSettings()
	{
		s_Settings = Settings();
		s_SettingsLoaded = true;
		std::ifstream in(fs::path(SettingsFile()), std::ios::binary);
		if (!in) return;
		std::stringstream ss;
		ss << in.rdbuf();
		const json j = json::parse(ss.str(), nullptr, false, true);
		if (!j.is_object()) return;
		s_Settings.ProjectWideActions = Str(j, "projectWideActions");
		if (j.contains("settings") && j["settings"].is_object())
		{
			const json& s = j["settings"];
			s_Settings.UpdateMode = std::clamp(s.value("updateMode", 1), 1, 3);
			s_Settings.BackgroundBehavior = std::clamp(s.value("backgroundBehavior", 0), 0, 2);
			s_Settings.DefaultDeadzoneMin = s.value("defaultDeadzoneMin", 0.125f);
			s_Settings.DefaultDeadzoneMax = s.value("defaultDeadzoneMax", 0.925f);
			s_Settings.DefaultButtonPressPoint = s.value("defaultButtonPressPoint", 0.5f);
			s_Settings.ButtonReleaseThreshold = s.value("buttonReleaseThreshold", 0.75f);
			s_Settings.DefaultTapTime = s.value("defaultTapTime", 0.2f);
			s_Settings.DefaultSlowTapTime = s.value("defaultSlowTapTime", 0.5f);
			s_Settings.DefaultHoldTime = s.value("defaultHoldTime", 0.4f);
			s_Settings.TapRadius = s.value("tapRadius", 5.0f);
			s_Settings.MultiTapDelayTime = s.value("multiTapDelayTime", 0.75f);
		}
	}

	Settings& ProjectSettings()
	{
		if (!s_SettingsLoaded)
			ReloadProjectSettings();
		return s_Settings;
	}

	void SaveProjectSettings()
	{
		const Settings& s = ProjectSettings();
		ojson j;
		j["projectWideActions"] = s.ProjectWideActions;
		ojson st;
		st["updateMode"] = s.UpdateMode;
		st["backgroundBehavior"] = s.BackgroundBehavior;
		st["defaultDeadzoneMin"] = s.DefaultDeadzoneMin;
		st["defaultDeadzoneMax"] = s.DefaultDeadzoneMax;
		st["defaultButtonPressPoint"] = s.DefaultButtonPressPoint;
		st["buttonReleaseThreshold"] = s.ButtonReleaseThreshold;
		st["defaultTapTime"] = s.DefaultTapTime;
		st["defaultSlowTapTime"] = s.DefaultSlowTapTime;
		st["defaultHoldTime"] = s.DefaultHoldTime;
		st["tapRadius"] = s.TapRadius;
		st["multiTapDelayTime"] = s.MultiTapDelayTime;
		j["settings"] = st;
		const fs::path p(SettingsFile());
		std::error_code ec;
		fs::create_directories(p.parent_path(), ec);
		std::ofstream out(p, std::ios::binary | std::ios::trunc);
		if (out) out << j.dump(4);
	}

	// ---------------------------------------------------------------- .meta · C# 클래스
	ImportSettings LoadImportSettings(const std::string& relPath)
	{
		ImportSettings s;
		std::ifstream in(fs::path(FullPath(relPath) + L".meta"), std::ios::binary);
		if (!in) return s;
		std::stringstream ss;
		ss << in.rdbuf();
		const json j = json::parse(ss.str(), nullptr, false, true);
		if (!j.is_object()) return s;   // Unity 의 YAML .meta 는 무시
		s.GenerateWrapperCode = Bool(j, "generateWrapperCode");
		s.WrapperCodePath = Str(j, "wrapperCodePath");
		s.WrapperClassName = Str(j, "wrapperClassName");
		s.WrapperCodeNamespace = Str(j, "wrapperCodeNamespace");
		return s;
	}

	void SaveImportSettings(const std::string& relPath, const ImportSettings& s)
	{
		const fs::path p(FullPath(relPath) + L".meta");
		json j = json::object();
		{
			std::ifstream in(p, std::ios::binary);
			if (in)
			{
				std::stringstream ss;
				ss << in.rdbuf();
				const json old = json::parse(ss.str(), nullptr, false, true);
				if (old.is_object()) j = old;
			}
		}
		j["generateWrapperCode"] = s.GenerateWrapperCode;
		j["wrapperCodePath"] = s.WrapperCodePath;
		j["wrapperClassName"] = s.WrapperClassName;
		j["wrapperCodeNamespace"] = s.WrapperCodeNamespace;
		std::ofstream out(p, std::ios::binary | std::ios::trunc);
		if (out) out << j.dump(4);
	}

	bool GenerateCSharp(const std::string& relPath, std::string* outPath, std::string* error)
	{
		Asset a;
		if (!a.Load(relPath, error))
			return false;
		const ImportSettings imp = LoadImportSettings(relPath);
		const std::wstring full = FullPath(relPath);
		const std::string stem = wstring_to_string(fs::path(full).stem().wstring());
		const std::string cls = TypeIdent(imp.WrapperClassName.empty() ? stem : imp.WrapperClassName);
		std::string csRel = imp.WrapperCodePath;
		if (csRel.empty())
		{
			std::string rel = relPath;
			const size_t dot = rel.rfind('.');
			csRel = rel.substr(0, dot) + ".cs";
		}
		const std::string ns = imp.WrapperCodeNamespace;
		const int base = ns.empty() ? 0 : 1;
		auto I = [&](int n) { return Indent(base + n); };

		std::ostringstream o;
		o << "//------------------------------------------------------------------------------\n"
			<< "// <auto-generated>\n"
			<< "//     NOVA Input System 이 " << relPath << " 에서 만든 코드입니다 (Unity 의 Generate C# Class 와 같은 모양).\n"
			<< "//     에셋을 저장하면 다시 만들어지므로 직접 고치지 마세요.\n"
			<< "// </auto-generated>\n"
			<< "//------------------------------------------------------------------------------\n\n"
			<< "using System;\nusing System.Collections;\nusing System.Collections.Generic;\nusing NovaEngine.InputSystem;\nusing NovaEngine.InputSystem.Utilities;\n\n";
		if (!ns.empty())
			o << "namespace " << ns << "\n{\n";
		o << I(0) << "public partial class @" << cls << " : IInputActionCollection2, IDisposable\n" << I(0) << "{\n";
		o << I(1) << "public InputActionAsset asset { get; }\n\n";
		o << I(1) << "public @" << cls << "()\n" << I(1) << "{\n";
		std::string jsonText = a.ToJson().dump(4);
		std::string escaped;
		for (char c : jsonText) { if (c == '"') escaped += "\"\""; else escaped += c; }
		o << I(2) << "asset = InputActionAsset.FromJson(@\"" << escaped << "\");\n";
		for (const Map& m : a.Maps)
		{
			const std::string mi = Identifier(m.Name);
			o << I(2) << "// " << m.Name << "\n";
			o << I(2) << "m_" << mi << " = asset.FindActionMap(" << CsString(m.Name) << ", throwIfNotFound: true);\n";
			for (const Action& act : m.Actions)
				o << I(2) << "m_" << mi << "_" << Identifier(act.Name) << " = m_" << mi << ".FindAction(" << CsString(act.Name) << ", throwIfNotFound: true);\n";
		}
		o << I(1) << "}\n\n";
		o << I(1) << "public void Dispose() => asset.Disable();\n"
			<< I(1) << "public InputBinding? bindingMask { get => asset.bindingMask; set => asset.bindingMask = value; }\n"
			<< I(1) << "public ReadOnlyArray<InputDevice>? devices { get => asset.devices; set => asset.devices = value; }\n"
			<< I(1) << "public ReadOnlyArray<InputControlScheme> controlSchemes => asset.controlSchemes;\n"
			<< I(1) << "public bool Contains(InputAction action) => asset.Contains(action);\n"
			<< I(1) << "public IEnumerator<InputAction> GetEnumerator() => asset.GetEnumerator();\n"
			<< I(1) << "IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();\n"
			<< I(1) << "public void Enable() => asset.Enable();\n"
			<< I(1) << "public void Disable() => asset.Disable();\n"
			<< I(1) << "public IEnumerable<InputBinding> bindings => asset.bindings;\n"
			<< I(1) << "public InputAction FindAction(string actionNameOrId, bool throwIfNotFound = false) => asset.FindAction(actionNameOrId, throwIfNotFound);\n"
			<< I(1) << "public int FindBinding(InputBinding bindingMask, out InputAction action) => asset.FindBinding(bindingMask, out action);\n";
		for (const Map& m : a.Maps)
		{
			const std::string mi = Identifier(m.Name), mt = TypeIdent(m.Name) + "Actions", it = "I" + TypeIdent(m.Name) + "Actions";
			o << "\n" << I(1) << "// " << m.Name << "\n";
			o << I(1) << "private readonly InputActionMap m_" << mi << ";\n";
			o << I(1) << "private readonly List<" << it << "> m_" << mt << "CallbackInterfaces = new List<" << it << ">();\n";
			for (const Action& act : m.Actions)
				o << I(1) << "private readonly InputAction m_" << mi << "_" << Identifier(act.Name) << ";\n";
			o << I(1) << "public struct " << mt << "\n" << I(1) << "{\n";
			o << I(2) << "private @" << cls << " m_Wrapper;\n";
			o << I(2) << "public " << mt << "(@" << cls << " wrapper) { m_Wrapper = wrapper; }\n";
			for (const Action& act : m.Actions)
				o << I(2) << "public InputAction @" << Identifier(act.Name) << " => m_Wrapper.m_" << mi << "_" << Identifier(act.Name) << ";\n";
			o << I(2) << "public InputActionMap Get() { return m_Wrapper.m_" << mi << "; }\n"
				<< I(2) << "public void Enable() { Get().Enable(); }\n"
				<< I(2) << "public void Disable() { Get().Disable(); }\n"
				<< I(2) << "public bool enabled => Get().enabled;\n"
				<< I(2) << "public static implicit operator InputActionMap(" << mt << " set) { return set.Get(); }\n";
			o << I(2) << "public void AddCallbacks(" << it << " instance)\n" << I(2) << "{\n"
				<< I(3) << "if (instance == null || m_Wrapper.m_" << mt << "CallbackInterfaces.Contains(instance)) return;\n"
				<< I(3) << "m_Wrapper.m_" << mt << "CallbackInterfaces.Add(instance);\n";
			for (const Action& act : m.Actions)
			{
				const std::string ai = Identifier(act.Name), cb = "On" + TypeIdent(act.Name);
				o << I(3) << "@" << ai << ".started += instance." << cb << ";\n"
					<< I(3) << "@" << ai << ".performed += instance." << cb << ";\n"
					<< I(3) << "@" << ai << ".canceled += instance." << cb << ";\n";
			}
			o << I(2) << "}\n\n";
			o << I(2) << "private void UnregisterCallbacks(" << it << " instance)\n" << I(2) << "{\n";
			for (const Action& act : m.Actions)
			{
				const std::string ai = Identifier(act.Name), cb = "On" + TypeIdent(act.Name);
				o << I(3) << "@" << ai << ".started -= instance." << cb << ";\n"
					<< I(3) << "@" << ai << ".performed -= instance." << cb << ";\n"
					<< I(3) << "@" << ai << ".canceled -= instance." << cb << ";\n";
			}
			o << I(2) << "}\n\n";
			o << I(2) << "public void RemoveCallbacks(" << it << " instance)\n" << I(2) << "{\n"
				<< I(3) << "if (m_Wrapper.m_" << mt << "CallbackInterfaces.Remove(instance)) UnregisterCallbacks(instance);\n"
				<< I(2) << "}\n\n";
			o << I(2) << "public void SetCallbacks(" << it << " instance)\n" << I(2) << "{\n"
				<< I(3) << "foreach (var item in m_Wrapper.m_" << mt << "CallbackInterfaces) UnregisterCallbacks(item);\n"
				<< I(3) << "m_Wrapper.m_" << mt << "CallbackInterfaces.Clear();\n"
				<< I(3) << "AddCallbacks(instance);\n"
				<< I(2) << "}\n";
			o << I(1) << "}\n";
			o << I(1) << "public " << mt << " @" << mi << " => new " << mt << "(this);\n";
		}
		for (const ControlScheme& s : a.Schemes)
		{
			const std::string si = TypeIdent(s.Name) + "Scheme";
			o << "\n" << I(1) << "private int m_" << si << "Index = -1;\n";
			o << I(1) << "public InputControlScheme " << si << "\n" << I(1) << "{\n" << I(2) << "get\n" << I(2) << "{\n"
				<< I(3) << "if (m_" << si << "Index == -1) m_" << si << "Index = asset.FindControlSchemeIndex(" << CsString(s.Name) << ");\n"
				<< I(3) << "return asset.controlSchemes[m_" << si << "Index];\n"
				<< I(2) << "}\n" << I(1) << "}\n";
		}
		for (const Map& m : a.Maps)
		{
			o << "\n" << I(1) << "public interface I" << TypeIdent(m.Name) << "Actions\n" << I(1) << "{\n";
			for (const Action& act : m.Actions)
				o << I(2) << "void On" << TypeIdent(act.Name) << "(InputAction.CallbackContext context);\n";
			o << I(1) << "}\n";
		}
		o << I(0) << "}\n";
		if (!ns.empty())
			o << "}\n";

		const fs::path csFull(FullPath(csRel));
		// 같은 내용이면 쓰지 않는다 (스크립트 다시 컴파일을 피한다)
		{
			std::ifstream in(csFull, std::ios::binary);
			if (in)
			{
				std::stringstream ss;
				ss << in.rdbuf();
				if (ss.str() == o.str())
				{
					if (outPath) *outPath = csRel;
					return true;
				}
			}
		}
		std::error_code ec;
		fs::create_directories(csFull.parent_path(), ec);
		std::ofstream out(csFull, std::ios::binary | std::ios::trunc);
		if (!out)
		{
			if (error) *error = "cannot write " + csRel;
			return false;
		}
		out << o.str();
		if (outPath) *outPath = csRel;
		return true;
	}

	void OnAssetSaved(const std::string& relPath)
	{
		if (!LoadImportSettings(relPath).GenerateWrapperCode)
			return;
		std::string path, error;
		if (!GenerateCSharp(relPath, &path, &error))
			Debug::LogWarning("Generate C# Class (" + relPath + "): " + error);
	}
}
