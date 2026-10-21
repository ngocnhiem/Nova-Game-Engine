#pragma once
#include "EditorWindow.h"
#include "InputActionsAsset.h"
#include <set>

// Unity 의 Input Actions 편집 창 (.inputactions 두 번 클릭 · Window > Input Actions · Project Settings > Input System Package 의 Edit).
//  - 위: Save Asset · Auto-Save · 컨트롤 스킴 (All Control Schemes / 스킴 / 추가 · 편집 · 지우기) · 검색
//  - 왼쪽: Action Maps (+ · 이름 바꾸기 · 복제 · 지우기)
//  - 가운데: Actions — 동작 아래 바인딩 · 합성 (WASD) 과 부분. + = Add Binding · 합성 (1D Axis · 2D Vector · 3D Vector · 수정 키)
//  - 오른쪽: 고른 동작 (Action Type · Control Type · Initial State Check · Interactions · Processors)
//           또는 바인딩 (Path — 목록 · Listen · 글자, Composite Part, Use in control scheme, Interactions · Processors), 합성 (Composite Type · Mode)
class InputActionsEditorWindow
	: public EditorWindow
{
public:
	InputActionsEditorWindow();

	static void Open(const std::string& path);
	// 열린 에셋에서 고르기 (CLI: nova inputactions window <path> --action Player/Move --binding 2) — bindingIndex = 동작 바인딩 안 번호, -1 = 동작
	static bool Select(const std::string& action, int bindingIndex);
	static bool IsOpen();
	static void RegisterEditor();   // .inputactions 에셋 (Create · 두 번 클릭 · Inspector) · PlayerInput 의 Edit Asset
	static void RegisterCli();      // nova inputactions · nova input (에디터 · 플레이어)
	// Project Settings > Input System Package
	static void DrawProjectSettings();

protected:
	void BeforeBegin() override;
	void PushStyle() override;
	void PopStyle() override;
	void OnRender() override;

private:
	static InputActionsEditorWindow* s_Instance;
	std::string m_Path;
	InputActions::Asset m_Asset;
	bool m_Loaded = false;
	bool m_Dirty = false;
	bool m_AutoSave = false;
	bool m_FocusNext = false;
	int m_Map = 0;
	int m_Action = -1;
	int m_Binding = -1;                 // -1 = 동작을 고름
	int m_Scheme = -1;                  // -1 = All Control Schemes
	char m_Search[96] = {};
	std::set<std::string> m_Collapsed;  // 접은 동작 · 합성 (id)
	int m_RenameKind = 0;               // 1 맵, 2 동작, 3 합성 이름
	int m_RenameIndex = -1;
	char m_RenameBuf[128] = {};
	int m_RenameFrames = 0;
	bool m_PathText = false;            // Path 를 글자로 고친다 (T)
	bool m_Listening = false;
	std::vector<std::string> m_ListenCandidates;
	uint8_t m_ListenKeys[256] = {};
	uint32 m_ListenPads[4] = {};
	float m_ListenAxes[4][6] = {};
	// 스킴 편집 창
	bool m_SchemeEditOpen = false;
	int m_SchemeEditIndex = -1;         // -1 = 새로
	InputActions::ControlScheme m_SchemeEdit;
	std::vector<InputActions::Asset> m_Undo;

	bool EnsureLoaded();
	void Save();
	void Changed();
	void PushUndo();
	void DrawToolbar(float width);
	void DrawMaps(float height);
	void DrawActions(float height);
	void DrawProperties(float height);
	void DrawActionProperties(InputActions::Map& map, InputActions::Action& action);
	void DrawBindingProperties(InputActions::Map& map, int binding);
	void DrawCompositeProperties(InputActions::Map& map, int binding);
	void DrawSchemeEditor();
	bool PathField(std::string& path, const std::string& expectedControlType);
	bool NamedList(const char* title, std::string& text, bool processors, const std::string& valueType);
	void AddBindingMenu(InputActions::Map& map, const std::string& action);
	bool BindingVisible(const InputActions::Map& map, int index) const;
	void UpdateListen(const std::string& expectedControlType);
};
