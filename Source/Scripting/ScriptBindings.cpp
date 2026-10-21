#include "pch.h"
#include "SkinnedMeshRenderer.h"
#include "SpriteRenderer.h"
#include "SpriteAnimator.h"
#include "Physics2DComponents.h"
#include "Physics2DJoints.h"
#include "Physics2DManager.h"
#include "Physics2DSettings.h"
#include "TagsAndLayers.h"
#include "PhysicsSettings.h"
#include "Debug.h"
#include "Profiler.h"
#include "JobSystem.h"
#include <unordered_set>
#include "ScriptBindings.h"
#include "ScriptEngine.h"
#include "CSharpScript.h"
#include "GameViewEditorWindow.h"
#include "GameObjectFactory.h"
#include "ComponentFactory.h"
#include "RigidBody.h"
#include "CharacterController.h"
#include "Joint.h"
#include "Collider.h"
#include "BoxCollider.h"
#include "SphereCollider.h"
#include "CapsuleCollider.h"
#include "AudioSource.h"
#include "AudioClip.h"
#include "AudioManager.h"
#include "AudioMixer.h"
#include "PhysicsManager.h"
#include "UIScriptBindings.h"
#include "UISystem.h"
#include "BuildSettings.h"
#include "PlayerPrefsStore.h"
#include "Light.h"
#include "Light2D.h"
#include "Ragdoll.h"
#include "WheelCollider.h"
#include "Cloth.h"
#include "Mesh.h"
#include "MeshFilter.h"
#include "Camera.h"
#include "PathManager.h"
#include "PlayerRuntime.h"
#include "ParticleSystem.h"

// C# 쪽 NativeApiTable(ScriptCore/Interop/NativeApi.cs)과 같은 순서·형식. 하나라도 어긋나면 Initialize 가 크기 불일치로 거부한다.
namespace
{
	using u8 = const char;   // C# 의 byte* (UTF-8, 널 끝)

	struct TimeData
	{
		float deltaTime, unscaledDeltaTime, time, unscaledTime, fixedDeltaTime, timeScale, realtimeSinceStartup;
		int frameCount;
	};
	struct RaycastData
	{
		Vec3 point;
		Vec3 normal;
		float distance;
		uint64 gameObject;
	};
	struct Ray2DData   // C# Ray2DData 와 같은 배치 (32 바이트)
	{
		Vec2 point;
		Vec2 normal;
		float distance;
		float fraction;
		uint64 gameObject;
	};
	// WheelCollider.GetGroundHit (C# NativeApi.cs 의 WheelHitData 와 같은 배치)
	struct WheelHitData
	{
		Vec3 point, normal, forwardDir, sidewaysDir;
		float force, forwardSlip, sidewaysSlip;
		uint64 gameObject;
	};

	struct ControllerHitData
	{
		Vec3 point;
		Vec3 normal;
		Vec3 moveDirection;
		float moveLength;
		uint64 gameObject;
	};

	// C# Job System · Mesh 바인딩의 형식 (아래 표가 쓴다 — 함수는 이 파일 뒤쪽)
	struct TransformAccessData;
	struct RaycastCommandData;
	struct NativeApiTable
	{
		int Size;
		void(*Log)(int, u8*, u8*, u8*, int);

		int(*GO_IsValid)(uint64);
		u8* (*GO_GetName)(uint64);
		void(*GO_SetName)(uint64, u8*);
		int(*GO_GetActive)(uint64, int);
		void(*GO_SetActive)(uint64, int);
		u8* (*GO_GetTag)(uint64);
		void(*GO_SetTag)(uint64, u8*);
		uint64(*GO_Find)(u8*, int);
		uint64(*GO_Create)(u8*);
		uint64(*GO_CreatePrimitive)(int);
		void(*GO_Destroy)(uint64, float);
		uint64(*GO_Instantiate)(uint64, int, Vec3*, Quaternion*, uint64);
		int(*GO_HasComponent)(uint64, u8*);
		void* (*GO_AddComponent)(uint64, u8*);
		void(*GO_RemoveComponent)(uint64, u8*);

		void(*TR_GetVector)(uint64, int, Vec3*);
		void(*TR_SetVector)(uint64, int, Vec3*);
		void(*TR_GetQuat)(uint64, int, Quaternion*);
		void(*TR_SetQuat)(uint64, int, Quaternion*);
		uint64(*TR_GetParent)(uint64);
		void(*TR_SetParent)(uint64, uint64, int);
		int(*TR_GetChildCount)(uint64);
		uint64(*TR_GetChild)(uint64, int);

		void(*Time_Get)(TimeData*);
		int(*Input_GetKey)(int, int);
		int(*Input_GetMouseButton)(int, int);
		void(*Input_GetMouse)(Vec4*);
		void(*Screen_Get)(int*, int*);

		void(*RB_GetVector)(uint64, int, Vec3*);
		void(*RB_SetVector)(uint64, int, Vec3*);
		void(*RB_AddForce)(uint64, int, Vec3*, int);
		float(*RB_GetFloat)(uint64, int);
		void(*RB_SetFloat)(uint64, int, float);
		int(*RB_GetBool)(uint64, int);
		void(*RB_SetBool)(uint64, int, int);
		void(*RB_Move)(uint64, int, Vec4*);

		void(*AS_Call)(uint64, int);
		float(*AS_GetFloat)(uint64, int);
		void(*AS_SetFloat)(uint64, int, float);
		int(*AS_GetBool)(uint64, int);
		void(*AS_SetBool)(uint64, int, int);
		void(*AS_PlayOneShot)(uint64, u8*, float);
		u8* (*AS_GetClip)(uint64);
		void(*AS_SetClip)(uint64, u8*);
		float(*Audio_ClipLength)(u8*);

		int(*PH_Raycast)(Vec3*, Vec3*, float, RaycastData*);
		uint64(*Camera_Main)();

		void(*Script_SetEnabled)(void*, int);

		// UI (Source/UI/UIScriptBindings.cpp)
		int(*UI_GetVec)(uint64, int, Vec4*);
		void(*UI_SetVec)(uint64, int, Vec4*);
		u8* (*UI_GetString)(uint64, int);
		void(*UI_SetString)(uint64, int, u8*);

		// 씬 / 앱
		int(*Scene_Load)(u8*, int);
		u8* (*Scene_Active)(int*);
		u8* (*Scene_PathAt)(int);
		int(*Scene_Count)();
		int(*App_Info)(int);
		u8* (*App_ProductName)();
		void(*App_Quit)();

		// Particle System
		void(*PS_Call)(uint64, int, int);
		float(*PS_GetFloat)(uint64, int);
		void(*PS_SetFloat)(uint64, int, float);
		void(*PS_GetCurve)(uint64, int, Vec4*);
		void(*PS_SetCurve)(uint64, int, int, float, float);
		int(*PS_GetColor)(uint64, int, Vec4*, Vec4*);
		void(*PS_SetColor)(uint64, int, int, Vec4*, Vec4*);

		// Character Controller
		int(*CC_Move)(uint64, Vec3*, float, int);
		float(*CC_GetFloat)(uint64, int);
		void(*CC_SetFloat)(uint64, int, float);
		void(*CC_GetVector)(uint64, int, Vec3*);
		void(*CC_SetVector)(uint64, int, Vec3*);
		int(*CC_GetInt)(uint64, int);
		void(*CC_SetInt)(uint64, int, int);
		int(*CC_GetHit)(uint64, int, ControllerHitData*);

		// Joint (kind 0 Fixed, 1 Hinge, 2 Spring — 같은 GameObject 의 첫 번째)
		float(*JT_GetFloat)(uint64, int, int);
		void(*JT_SetFloat)(uint64, int, int, float);
		void(*JT_GetVector)(uint64, int, int, Vec3*);
		void(*JT_SetVector)(uint64, int, int, Vec3*);
		uint64(*JT_GetConnected)(uint64, int);
		void(*JT_SetConnected)(uint64, int, uint64);
		u8*(*AS_GetOutput)(uint64);
		void(*AS_SetOutput)(uint64, u8*, u8*);
		int(*MX_Load)(u8*);
		int(*MX_SetFloat)(u8*, u8*, float);
		int(*MX_GetFloat)(u8*, u8*, float*);
		int(*MX_ClearFloat)(u8*, u8*);
		int(*MX_Transition)(u8*, u8*, float);
		u8*(*MX_Names)(u8*, int);
		float(*MX_GroupLevel)(u8*, u8*);
		// Text (TextMeshPro 기능) — UIScriptBindings::TextInfo / TextLink
		int(*TX_Info)(uint64, int, int, float*, int);
		u8* (*TX_Link)(uint64, int, int);
		uint64(*UI_RaycastScreen)(float, float);
		// SkinnedMeshRenderer BlendShape (Unity: SetBlendShapeWeight · sharedMesh.blendShapeCount · GetBlendShapeName · GetBlendShapeIndex)
		int(*SMR_Count)(uint64);
		u8* (*SMR_Name)(uint64, int);
		int(*SMR_Index)(uint64, u8*);
		float(*SMR_GetWeight)(uint64, int);
		void(*SMR_SetWeight)(uint64, int, float);
		// SpriteRenderer (Unity: color · flipX · flipY · sortingOrder · sprite)
		int(*SR_GetColor)(uint64, float*);
		void(*SR_SetColor)(uint64, float*);
		int(*SR_GetInt)(uint64, int);          // 0 flipX, 1 flipY, 2 sortingOrder
		void(*SR_SetInt)(uint64, int, int);
		u8* (*SR_GetSprite)(uint64);
		void(*SR_SetSprite)(uint64, u8*);
		// 레이어 (Unity: gameObject.layer · LayerMask · Physics.Raycast(layerMask) · IgnoreLayerCollision · gravity)
		int(*GO_GetLayer)(uint64);
		void(*GO_SetLayer)(uint64, int);
		int(*LM_NameToLayer)(u8*);
		u8* (*LM_LayerToName)(int);
		int(*PH_RaycastMask)(Vec3*, Vec3*, float, int, int, RaycastData*);   // mask, hitTriggers
		void(*PH_IgnoreLayer)(int, int, int);
		int(*PH_GetIgnoreLayer)(int, int);
		void(*PH_GetGravity)(Vec3*);
		void(*PH_SetGravity)(Vec3*);
		// Sprite Animator (프레임 애니메이션) + Sorting Layer 이름
		int(*SA_Play)(uint64, u8*);
		void(*SA_Stop)(uint64);
		int(*SA_GetInt)(uint64, int);            // 0 isPlaying, 1 frame
		u8* (*SA_Clip)(uint64);
		float(*SA_GetSpeed)(uint64);
		void(*SA_SetSpeed)(uint64, float);
		u8* (*SR_GetSortingLayer)(uint64);
		int(*SR_SetSortingLayer)(uint64, u8*);
		// Camera.cullingMask (which 0) · Light.cullingMask (which 1)
		int(*CL_GetMask)(uint64, int);
		void(*CL_SetMask)(uint64, int, int);
		// 2D 물리 (Rigidbody2D · Collider2D · Physics2D)
		void(*R2_GetVec)(uint64, int, Vec2*);          // 0 velocity, 1 position
		void(*R2_SetVec)(uint64, int, Vec2*);
		float(*R2_GetFloat)(uint64, int);              // 0 angularVelocity, 1 rotation, 2 mass, 3 gravityScale, 4 linearDamping, 5 angularDamping, 6 bodyType, 7 freezeRotation, 8 collisionDetection
		void(*R2_SetFloat)(uint64, int, float);
		void(*R2_Act)(uint64, int, Vec2*, Vec2*, float, int);   // 0 AddForce, 1 AddForceAtPosition, 2 AddTorque, 3 MovePosition, 4 MoveRotation (mode 1 = Impulse)
		int(*C2_Get)(uint64, u8*, int, float*);        // 0 isTrigger, 1 offset, 2 size, 3 radius, 4 friction, 5 bounciness, 6 enabled
		void(*C2_Set)(uint64, u8*, int, float*);
		int(*P2_Raycast)(Vec2*, Vec2*, float, int, Ray2DData*);
		uint64(*P2_Overlap)(int, Vec2*, Vec2*, float, int);   // 0 point, 1 circle (value = radius), 2 box (size, value = angle)
		void(*P2_Gravity)(int, Vec2*);                 // 0 get, 1 set
		void(*P2_IgnoreLayer)(int, int, int);
		int(*P2_GetIgnoreLayer)(int, int);
		int(*P2_Contact)(uint64, uint64, float*);      // self, other → point xy, normal xy, relativeVelocity xy
		float(*J2_GetFloat)(uint64, int, int, int);
		void(*J2_SetFloat)(uint64, int, int, int, float);
		void(*J2_GetVec)(uint64, int, int, int, Vec2*);
		void(*J2_SetVec)(uint64, int, int, int, Vec2*);
		uint64(*J2_GetConnected)(uint64, int, int);
		void(*J2_SetConnected)(uint64, int, int, uint64);
		int(*J2_Find)(uint64, int, int);
		void(*J2_Remove)(uint64, int, int);
		// 여러 씬 (SceneManagerRuntime.cpp)
		int(*Scene_LoadOp)(u8*, int, int, int);           // 이름 | 빌드 번호, 모드 (0 Single · 1 Additive), 비동기 → 작업 번호 (0 = 없는 씬, -1 = Play 중 아님)
		int(*Scene_OpState)(int, float*, int*);           // 작업 → 1 끝 · 2 실패 · 4 allowSceneActivation (-1 = 모름), 진행, 씬 핸들
		void(*Scene_OpAllow)(int, int);
		int(*Scene_Unload)(int);                          // 핸들 → 작업 번호 (0 = 내릴 수 없음)
		int(*Scene_LoadedCount)();
		int(*Scene_HandleAt)(int);
		u8* (*Scene_HandleInfo)(int, int*, int*);         // 핸들 → 경로, 읽혀 있음, 루트 수
		int(*Scene_ActiveHandle)();
		int(*Scene_SetActive)(int);
		int(*Scene_Roots)(int, uint64*, int);
		int(*GO_SceneHandle)(uint64);
		int(*GO_MoveToScene)(uint64, int);                // 핸들 -1 = DontDestroyOnLoad
		// PlayerPrefs (PlayerPrefsStore) · Application 경로
		int(*Prefs_Has)(u8*);
		void(*Prefs_SetInt)(u8*, int);
		int(*Prefs_GetInt)(u8*, int);
		void(*Prefs_SetFloat)(u8*, float);
		float(*Prefs_GetFloat)(u8*, float);
		void(*Prefs_SetString)(u8*, u8*);
		u8* (*Prefs_GetString)(u8*);                      // 없으면 nullptr
		void(*Prefs_Delete)(u8*);                         // nullptr = 모두
		void(*Prefs_Save)();
		u8* (*App_Path)(int);                             // 0 persistentDataPath, 1 dataPath, 2 companyName, 3 version, 4 temporaryCachePath
		// Camera · Light 값 (C# 속성 · 트윈)
		float(*Cam_GetFloat)(uint64, int);                // 0 fieldOfView (도), 1 nearClipPlane, 2 farClipPlane, 3 orthographicSize, 4 aspect, 5 orthographic
		void(*Cam_SetFloat)(uint64, int, float);
		float(*Light_GetFloat)(uint64, int);              // 0 intensity, 1 shadowStrength, 2 range, 3 spotAngle
		void(*Light_SetFloat)(uint64, int, float);
		void(*Light_GetColor)(uint64, Vec4*);
		void(*Light_SetColor)(uint64, Vec4*);
		// 2D 빛 (Light2D.h)
		float(*L2D_GetFloat)(uint64, int);                // 0 종류 (0 Global · 1 Spot), 1 intensity, 2 innerRadius, 3 outerRadius, 4 innerAngle, 5 outerAngle, 6 falloff, 7 shadows, 8 shadowStrength, 9 normalMapDistance
		void(*L2D_SetFloat)(uint64, int, float);
		void(*L2D_GetColor)(uint64, Vec4*);
		void(*L2D_SetColor)(uint64, Vec4*);
		int(*SC2D_Get)(uint64, int);                      // 0 castsShadows, 1 selfShadows
		void(*SC2D_Set)(uint64, int, int);
		float(*RD_Get)(uint64, int);                      // Ragdoll: 0 active, 1 바디 수 (읽기), 2 blendTime, 3 하늘을 봄 · 4 섞는 중 (읽기), 5 alignRoot
		void(*RD_Set)(uint64, int, float);
		void(*PH_IgnoreCollision)(uint64, uint64, int);   // Physics.IgnoreCollision (콜라이더의 GameObject 둘)
		int(*PH_GetIgnoreCollision)(uint64, uint64);
		// WheelCollider — float: 0 mass, 1 radius, 2 wheelDampingRate, 3 suspensionDistance, 4 forceAppPointDistance, 5 spring, 6 damper, 7 targetPosition,
		//   10~14 forwardFriction (extremumSlip, extremumValue, asymptoteSlip, asymptoteValue, stiffness), 20~24 sidewaysFriction,
		//   30 motorTorque, 31 brakeTorque, 32 steerAngle, 40 rpm, 41 isGrounded, 42 sprungMass (읽기)
		float(*WC_GetFloat)(uint64, int);
		void(*WC_SetFloat)(uint64, int, float);
		void(*WC_GetCenter)(uint64, Vec3*);
		void(*WC_SetCenter)(uint64, Vec3*);
		void(*WC_GetPose)(uint64, Vec3*, Vec4*);
		int(*WC_GetHit)(uint64, WheelHitData*);       // 바닥에 닿았으면 1
		// Cloth — float: 0 stretchingStiffness, 1 bendingStiffness, 2 useGravity, 3 damping, 4 friction, 5 thickness, 6 solverFrequency, 7 pin,
		//   8 시뮬레이션 중 (읽기), 9 정점 수 (읽기) / vector: 0 externalAcceleration, 1 randomAcceleration
		float(*CL_GetFloat)(uint64, int);
		void(*CL_SetFloat)(uint64, int, float);
		void(*CL_GetVector)(uint64, int, Vec3*);
		void(*CL_SetVector)(uint64, int, Vec3*);
		int(*CL_GetVertices)(uint64, Vec3*, int);      // 메시 정점마다 로컬 위치 (Unity Cloth.vertices) — 개수를 돌려준다
		// Behaviour.enabled · Collider.enabled: 네이티브 컴포넌트 (타입 이름 — GO_HasComponent 와 같다) 의 Inspector 체크 상자. 없으면 -1
		int(*Comp_GetEnabled)(uint64, u8*);
		void(*Comp_SetEnabled)(uint64, u8*, int);
		// Cloth.coefficients (Unity ClothSkinningCoefficient): 천 정점마다 [maxDistance, collisionSphereDistance] — 개수를 돌려준다 (FLT_MAX = 제한 없음)
		int(*CL_GetCoefficients)(uint64, float*, int);
		void(*CL_SetCoefficients)(uint64, float*, int);
		// Application.targetFrameRate (set != 0 이면 value 로 바꾼다) — 지금 값을 돌려준다
		int(*App_TargetFrameRate)(int, int);
		// Application.backgroundLoadingPriority (ThreadPriority 값 — set != 0 이면 바꾼다)
		int(*App_LoadingPriority)(int, int);
		// Collider 값 (Unity: isTrigger · center · BoxCollider.size · SphereCollider.radius · CapsuleCollider.radius · height · direction).
		//  type = 네이티브 타입 이름, prop: 0 isTrigger 1 center 2 size 3 Sphere radius 4 Capsule radius 5 height 6 direction, 값은 float 4 개
		void(*COL_Get)(uint64, u8*, int, float*);
		void(*COL_Set)(uint64, u8*, int, float*);

		// Profiler (C# ProfilerMarker · Profiler.BeginSample · C# Job System 작업자) — 이름은 C# 이 붙잡아 둔 UTF-8
		int(*Prof_Begin)(const char*, int64_t*, uint16_t*);   // 0 꺼짐, 1 메인 스레드 (중첩 스택), 2 다른 스레드 (시작 시각을 돌려줌)
		void(*Prof_End)(int, const char*, int64_t, uint16_t);
		void(*Prof_SetThreadName)(const char*);

		// C# Job System 과 엔진: TransformAccessArray (모으기 · 되쓰기), RaycastCommand (묶음), C# Mesh (런타임 메시)
		int(*TR_Gather)(const uint64*, int, TransformAccessData*);
		int(*TR_Scatter)(const uint64*, int, const TransformAccessData*);
		int(*PH_RaycastBatch)(const RaycastCommandData*, int, RaycastData*, int);
		int(*MESH_Create)();
		void(*MESH_Release)(int);
		int(*MESH_FromFilter)(uint64);
		int(*MESH_SetToFilter)(uint64, int);
		int(*MESH_GetInt)(int, int);
		u8* (*MESH_GetName)(int);
		void(*MESH_SetName)(int, u8*);
		int(*MESH_SetAttr)(int, int, const float*, int, int, int);
		int(*MESH_GetAttr)(int, int, float*, int, int);
		int(*MESH_SetIndices)(int, const void*, int, int);
		int(*MESH_GetIndices)(int, int*, int);
		void(*MESH_Op)(int, int);
		void(*MESH_Bounds)(int, Vec3*, Vec3*);
	};

	// ---------------------------------------------------------------- 공용
	std::unordered_map<uint64, GameObject*> s_Cache;   // 프레임마다 비움
	uint64 s_CacheSceneSerial = 0;
	struct PendingAdd { GameObject* Object; uint64 Parent; bool WorldStays; };
	std::vector<GameObject*> s_Created;                 // 이번 프레임에 만든 (아직 씬 목록에 없는) 오브젝트
	struct DelayedDestroy { uint64 Id; float Time; };
	std::vector<DelayedDestroy> s_DelayedDestroys;
	double s_StartTime = 0.0;

	const char* Ret(const std::string& s)
	{
		// 관리 코드가 바로 복사해 가므로 몇 개를 돌려 쓴다
		static std::string ring[8];
		static int i = 0;
		i = (i + 1) & 7;
		ring[i] = s;
		return ring[i].c_str();
	}

	Scene* CurrentScene() { return SceneManager::GetI()->GetCurrentScene(); }

	GameObject* Find(uint64 id)
	{
		if (id == 0)
			return nullptr;
		Scene* scene = CurrentScene();
		const uint64 serial = scene ? scene->GetSerial() : 0;
		if (serial != s_CacheSceneSerial) { s_Cache.clear(); s_CacheSceneSerial = serial; }
		auto it = s_Cache.find(id);
		if (it != s_Cache.end())
		{
			if (GameObject::IsAlive(it->second) && it->second->GetFileID() == id) return it->second;
			s_Cache.erase(it); // Undo 는 씬을 바꾸지 않고 루트 오브젝트를 바꿀 수 있다
		}
		GameObject* g = scene ? scene->FindByFileID(id) : nullptr;
		if (g == nullptr)
			for (GameObject* c : s_Created)
				if (GameObject::IsAlive(c) && c->GetFileID() == id) { g = c; break; }
		if (g)
			s_Cache[id] = g;
		return g;
	}

	void CacheTree(GameObject* g)
	{
		s_Cache[g->GetFileID()] = g;
		for (GameObject* c : g->GetChildren())
			CacheTree(c);
	}

	void ForgetTree(GameObject* g)
	{
		s_Cache.erase(g->GetFileID());
		for (GameObject* c : g->GetChildren())
			ForgetTree(c);
	}

	bool ActiveInHierarchy(GameObject* go)
	{
		for (GameObject* g = go; g != nullptr; g = g->GetParent())
			if (!g->IsActive())
				return false;
		return true;
	}

	// 새로 만든 오브젝트: 캐시에 넣고 씬 목록에는 프레임 끝에 (업데이트 중 목록을 바꾸면 반복이 깨진다)
	void AddToSceneLater(GameObject* g, uint64 parent, bool worldStays)
	{
		s_Created.push_back(g);
		CacheTree(g);
		SceneManager::GetI()->AddLastUpdate([g, parent, worldStays]() {
			s_Created.erase(std::remove(s_Created.begin(), s_Created.end(), g), s_Created.end());
			Scene* scene = CurrentScene();
			if (scene == nullptr)
				return;
			// 그 사이 스크립트가 transform.SetParent 로 부모를 정했으면 루트로 넣지 않는다 (씬 목록 등록만)
			if (g->GetParent() != nullptr)
				scene->RegisterGameObjectTree(g);
			else
			{
				scene->AddRootGameObject(g);
				SceneManager::GetI()->OnRuntimeRootCreated(g);   // 활성 씬 (SceneManager.SetActiveScene)
			}
			if (parent)
				if (GameObject* p = scene->FindByFileID(parent))
					g->SetParent(p, worldStays);
		});
	}

	// Play 중 새 오브젝트: Awake (스크립트는 인스턴스 생성) — Start 는 첫 Update 직전
	void AwakeTree(GameObject* g)
	{
		if (!Application::IsPlaying())
			return;
		for (const auto& c : g->GetComponents())
		{
			if (auto* s = dynamic_cast<CSharpScript*>(c.get()))
				s->AwakeNow();
			else
			{
				c->Awake();
				c->Start();
			}
		}
		for (GameObject* child : g->GetChildren())
			AwakeTree(child);
	}

	void DestroyNow(uint64 id)
	{
		SceneManager::GetI()->AddLastUpdate([id]() {
			Scene* scene = CurrentScene();
			GameObject* g = scene ? scene->FindByFileID(id) : nullptr;
			if (g == nullptr)
				return;
			ForgetTree(g);
			scene->DestroyGameObject(g);
		});
	}

	Component* FindComponent(GameObject* g, const std::string& type)
	{
		if (g == nullptr)
			return nullptr;
		auto match = [&](Component* c) -> bool {
			if (type.rfind("script:", 0) == 0)
			{
				auto* s = dynamic_cast<CSharpScript*>(c);
				const std::string cls = type.substr(7);
				return s && (s->GetClassName() == cls || s->GetClassName().size() > cls.size() && s->GetClassName().compare(s->GetClassName().size() - cls.size() - 1, std::string::npos, "." + cls) == 0);
			}
			if (type == "Collider")
				return dynamic_cast<Collider*>(c) != nullptr;
			if (type == "Collider2D")
				return dynamic_cast<Collider2D*>(c) != nullptr;
			return c->GetType() == type;
		};
		for (const auto& c : g->GetComponents())
			if (c && match(c.get()))
				return c.get();
		for (const auto& c : g->GetPendingComponents())   // 같은 프레임에 AddComponent 한 것
			if (c && match(c.get()))
				return c.get();
		return nullptr;
	}

	// 같은 프레임에 AddComponent 한 것도 (Unity 처럼 AddComponent<SpriteRenderer>().sprite = … 가 바로 먹는다)
	template <typename T> T* Get(uint64 id) { GameObject* g = Find(id); return g ? g->GetComponentIncludingPending<T>() : nullptr; }
	Rigidbody2D* Get2DBody(uint64 id) { auto* g = Find(id); return GameObject::IsAlive(g) ? g->GetComponentIncludingPending<Rigidbody2D>() : nullptr; }
	Joint2D* FindJoint2D(uint64 id, int kind, int instance, int index = 0)
	{
		auto* go = Find(id);
		if (!GameObject::IsAlive(go)) return nullptr;
		for (const auto* list : { static_cast<const vector<shared_ptr<Component>>*>(&go->GetComponents()), &go->GetPendingComponents() })
			for (const auto& c : *list)
				if (auto* j = dynamic_cast<Joint2D*>(c.get()); j && (int)j->JointKind() == kind)
				{
					if (instance ? j->GetInstanceID() == instance : index-- == 0) return j;
				}
		return nullptr;
	}

	ForceMode ToForceMode(int m)
	{
		switch (m)
		{
		case 1: return ForceMode::Impulse;
		case 2: return ForceMode::VelocityChange;
		case 3: return ForceMode::Acceleration;
		default: return ForceMode::Force;
		}
	}

	// ---------------------------------------------------------------- 함수들
	void Log(int level, u8* msg, u8* stack, u8* file, int line)
	{
		Debug::Write((LogType)std::clamp(level, 0, 2), msg ? msg : "", stack ? stack : "", file ? file : "", line);
	}

	int GO_IsValid(uint64 id) { return Find(id) != nullptr; }
	u8* GO_GetName(uint64 id) { GameObject* g = Find(id); return Ret(g ? g->GetName() : std::string()); }
	void GO_SetName(uint64 id, u8* n) { if (GameObject* g = Find(id)) g->SetName(n ? n : ""); }
	int GO_GetActive(uint64 id, int hierarchy) { GameObject* g = Find(id); return g ? (hierarchy ? ActiveInHierarchy(g) : g->IsActive()) : 0; }
	void GO_SetActive(uint64 id, int v) { if (GameObject* g = Find(id)) g->SetActive(v != 0); }
	u8* GO_GetTag(uint64 id) { GameObject* g = Find(id); return Ret(g ? g->GetTag() : std::string("Untagged")); }
	void GO_SetTag(uint64 id, u8* t) { if (GameObject* g = Find(id)) g->SetTag(t ? t : "Untagged"); }

	uint64 GO_Find(u8* key, int byTag)
	{
		Scene* scene = CurrentScene();
		if (scene == nullptr || key == nullptr)
			return 0;
		const std::string k = key;
		auto test = [&](GameObject* g) { return ActiveInHierarchy(g) && (byTag ? g->GetTag() == k : g->GetName() == k); };
		for (GameObject* g : scene->GetAllGameObjects())
			if (g && test(g))
				return g->GetFileID();
		for (GameObject* g : s_Created)
			if (test(g))
				return g->GetFileID();
		return 0;
	}

	uint64 GO_Create(u8* name)
	{
		GameObject* g = GameObjectFactory::CreateEmpty(name ? name : "New Game Object");
		AddToSceneLater(g, 0, true);
		return g->GetFileID();
	}

	uint64 GO_CreatePrimitive(int type)
	{
		GameObject* g = nullptr;
		switch (type)   // Unity PrimitiveType: Sphere, Capsule, Cylinder, Cube, Plane, Quad
		{
		case 0: g = GameObjectFactory::CreateSphere(); break;
		case 1: g = GameObjectFactory::CreateCapsule(); break;
		case 2: g = GameObjectFactory::CreateCylinder(); break;
		case 3: g = GameObjectFactory::CreateCube(); break;
		case 4: g = GameObjectFactory::CreatePlane(); break;
		default: g = GameObjectFactory::CreateQuad(); break;
		}
		AddToSceneLater(g, 0, true);
		AwakeTree(g);
		return g->GetFileID();
	}

	void GO_Destroy(uint64 id, float delay)
	{
		if (delay > 0.0f)
			s_DelayedDestroys.push_back({ id, ScriptEngine::PlayTime() + delay });
		else
			DestroyNow(id);
	}

	uint64 GO_Instantiate(uint64 src, int hasPose, Vec3* pos, Quaternion* rot, uint64 parent)
	{
		GameObject* s = Find(src);
		if (s == nullptr)
			return 0;
		json j = *s;
		GameObject* g = new GameObject();
		from_json(j, *g);
		g->RegenerateFileIDs();
		g->SetName(s->GetName() + "(Clone)");
		if (hasPose && pos && rot)
		{
			g->GetTransform()->SetPosition(*pos);
			g->GetTransform()->SetRotation(*rot);
		}
		// 위치를 주면 월드 기준 유지, 부모만 주면 원본의 로컬 값을 부모 기준으로 (Unity 의 instantiateInWorldSpace = false)
		AddToSceneLater(g, parent, hasPose != 0);
		AwakeTree(g);
		return g->GetFileID();
	}

	int GO_HasComponent(uint64 id, u8* type) { return FindComponent(Find(id), type ? type : "") != nullptr; }
	int Comp_GetEnabled(uint64 id, u8* type)
	{
		Component* c = FindComponent(Find(id), type ? type : "");
		return c == nullptr ? -1 : (c->IsEnabled() ? 1 : 0);
	}
	// Collider 값 — 설정 함수가 물리 번호를 올린다 (다음 스텝에 바디가 따른다)
	void COL_Get(uint64 id, u8* type, int prop, float* out)
	{
		if (out == nullptr)
			return;
		out[0] = out[1] = out[2] = out[3] = 0.0f;
		Collider* c = dynamic_cast<Collider*>(FindComponent(Find(id), type ? type : ""));
		if (c == nullptr)
			return;
		Vec3 v = Vec3::Zero;
		switch (prop)
		{
		case 0: v.x = c->IsTrigger() ? 1.0f : 0.0f; break;
		case 1: v = c->GetCenter(); break;
		case 2: if (auto* b = dynamic_cast<BoxCollider*>(c)) v = b->GetSize(); break;
		case 3: if (auto* s = dynamic_cast<SphereCollider*>(c)) v.x = s->GetRadius(); break;
		case 4: if (auto* k = dynamic_cast<CapsuleCollider*>(c)) v.x = k->GetRadius(); break;
		case 5: if (auto* k = dynamic_cast<CapsuleCollider*>(c)) v.x = k->GetHeight(); break;
		case 6: if (auto* k = dynamic_cast<CapsuleCollider*>(c)) v.x = (float)k->GetDirection(); break;
		default: break;
		}
		out[0] = v.x; out[1] = v.y; out[2] = v.z;
	}
	void COL_Set(uint64 id, u8* type, int prop, float* in)
	{
		Collider* c = in ? dynamic_cast<Collider*>(FindComponent(Find(id), type ? type : "")) : nullptr;
		if (c == nullptr)
			return;
		const Vec3 v(in[0], in[1], in[2]);
		switch (prop)
		{
		case 0: c->SetIsTrigger(v.x != 0.0f); break;
		case 1: c->SetCenter(v); break;
		case 2: if (auto* b = dynamic_cast<BoxCollider*>(c)) b->SetSize(v); break;
		case 3: if (auto* s = dynamic_cast<SphereCollider*>(c)) s->SetRadius(v.x); break;
		case 4: if (auto* k = dynamic_cast<CapsuleCollider*>(c)) k->SetRadius(v.x); break;
		case 5: if (auto* k = dynamic_cast<CapsuleCollider*>(c)) k->SetHeight(v.x); break;
		case 6: if (auto* k = dynamic_cast<CapsuleCollider*>(c)) k->SetDirection((int)v.x); break;
		default: break;
		}
	}

	void Comp_SetEnabled(uint64 id, u8* type, int on)
	{
		if (Component* c = FindComponent(Find(id), type ? type : ""))
			c->SetEnabled(on != 0);   // Unity: 끈 컴포넌트는 멈춘다 (각 컴포넌트가 IsEnabled 를 본다)
	}

	void* GO_AddComponent(uint64 id, u8* type)
	{
		GameObject* g = Find(id);
		if (g == nullptr || type == nullptr)
			return nullptr;
		const std::string t = type;
		if (t.rfind("script:", 0) == 0)
		{
			auto s = CSharpScript::Create(t.substr(7));
			g->QueueComponent(s);
			s->AwakeNow();
			return s->GetHandle();
		}
		auto c = ComponentFactory::Instance().CreateComponent(t);
		if (c == nullptr)
			return nullptr;
		UISystem::QueueRectTransformFor(g, c.get());   // Unity: UI 컴포넌트를 붙이면 RectTransform 도 바로 생긴다
		g->QueueComponent(c);
		if (Application::IsPlaying())
		{
			c->Awake();
			c->Start();
		}
		return (void*)1;
	}

	void GO_RemoveComponent(uint64 id, u8* type)
	{
		if (Component* c = FindComponent(Find(id), type ? type : ""))
			GameObject::Destroy(c);
	}

	void TR_GetVector(uint64 id, int prop, Vec3* out)
	{
		GameObject* g = Find(id);
		if (g == nullptr || out == nullptr) { if (out) *out = Vec3::Zero; return; }
		Transform* t = g->GetTransform();
		switch (prop)
		{
		case 0: *out = t->GetPosition(); break;
		case 1: *out = t->GetLocalPosition(); break;
		case 2: *out = t->GetLocalScale(); break;
		case 3: *out = t->GetEulerAngle(); break;
		case 4: *out = t->GetLocalEulerAngles(); break;
		case 5: *out = t->GetScale(); break;
		case 6: *out = t->GetForward(); break;
		case 7: *out = t->GetRight(); break;
		default: *out = t->GetUp(); break;
		}
	}

	void TR_SetVector(uint64 id, int prop, Vec3* v)
	{
		GameObject* g = Find(id);
		if (g == nullptr || v == nullptr)
			return;
		Transform* t = g->GetTransform();
		switch (prop)
		{
		case 0: t->SetPosition(*v); break;
		case 1: t->SetLocalPosition(*v); break;
		case 2: t->SetLocalScale(*v); break;
		case 3: t->SetEulerAngle(*v); break;
		case 4: t->SetLocalEulerAngles(*v); break;
		default: break;
		}
	}

	void TR_GetQuat(uint64 id, int prop, Quaternion* out)
	{
		GameObject* g = Find(id);
		if (out == nullptr) return;
		*out = g ? (prop == 0 ? g->GetTransform()->GetRotation() : g->GetTransform()->GetLocalRotation()) : Quaternion::Identity;
	}

	void TR_SetQuat(uint64 id, int prop, Quaternion* q)
	{
		GameObject* g = Find(id);
		if (g == nullptr || q == nullptr) return;
		if (prop == 0) g->GetTransform()->SetRotation(*q);
		else g->GetTransform()->SetLocalRotation(*q);
	}

	uint64 TR_GetParent(uint64 id) { GameObject* g = Find(id); return g && g->GetParent() ? g->GetParent()->GetFileID() : 0; }
	void TR_SetParent(uint64 id, uint64 parent, int worldStays)
	{
		GameObject* g = Find(id);
		if (g == nullptr) return;
		g->SetParent(parent ? Find(parent) : nullptr, worldStays != 0);
	}
	int TR_GetChildCount(uint64 id) { GameObject* g = Find(id); return g ? g->GetChildCount() : 0; }
	uint64 TR_GetChild(uint64 id, int index)
	{
		GameObject* g = Find(id);
		if (g == nullptr || index < 0 || index >= g->GetChildCount()) return 0;
		GameObject* c = g->GetChildren()[index];
		s_Cache[c->GetFileID()] = c;
		return c->GetFileID();
	}

	void Time_Get(TimeData* t)
	{
		if (t == nullptr) return;
		LARGE_INTEGER f, c;
		::QueryPerformanceFrequency(&f);
		::QueryPerformanceCounter(&c);
		const double now = (double)c.QuadPart / (double)f.QuadPart;
		// Unity 의 Time.maximumDeltaTime (기본 1/3 초): 긴 프레임 (Play 시작 · 로딩 · 디버거) 뒤 스크립트가 한 번에 멀리 움직이지 않게
		constexpr float kMaximumDeltaTime = 1.0f / 3.0f;
		const float dt = Application::IsPaused() ? 0.0f : (std::min)((float)DT, kMaximumDeltaTime);
		t->deltaTime = dt;
		t->unscaledDeltaTime = (float)DT;
		t->time = ScriptEngine::PlayTime();
		t->unscaledTime = ScriptEngine::PlayTime();
		t->fixedDeltaTime = PhysicsManager::GetI()->GetFixedTimestep();
		t->timeScale = 1.0f;
		t->realtimeSinceStartup = (float)(now - s_StartTime);
		t->frameCount = ScriptEngine::FrameCount();
	}

	int Input_GetKey(int vk, int mode) { return ScriptEngine::KeyState(vk, mode) ? 1 : 0; }
	int Input_GetMouseButton(int b, int mode) { return ScriptEngine::MouseButtonState(b, mode) ? 1 : 0; }
	void Input_GetMouse(Vec4* out)
	{
		if (out == nullptr) return;
		float x = 0, y = 0;
		GameViewEditorWindow::MouseToGame(x, y);
		*out = Vec4(x, y, 0.0f, GameViewEditorWindow::HasInputFocus() ? GameViewEditorWindow::ScrollDelta() : 0.0f);
	}
	void Screen_Get(int* w, int* h)
	{
		int ww = 0, hh = 0;
		GameViewEditorWindow::GameSize(ww, hh);
		if (w) *w = ww;
		if (h) *h = hh;
	}

	void RB_GetVector(uint64 id, int prop, Vec3* out)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (out == nullptr) return;
		if (rb == nullptr) { *out = Vec3::Zero; return; }
		*out = prop == 0 ? rb->GetVelocity() : (prop == 1 ? rb->GetAngularVelocity() : rb->GetWorldCenterOfMass());
	}
	void RB_SetVector(uint64 id, int prop, Vec3* v)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (rb == nullptr || v == nullptr) return;
		if (prop == 0) rb->SetVelocity(*v);
		else if (prop == 1) rb->SetAngularVelocity(*v);
	}
	void RB_AddForce(uint64 id, int kind, Vec3* v, int mode)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (rb == nullptr || v == nullptr) return;
		if (kind == 0) rb->AddForce(*v, ToForceMode(mode));
		else rb->AddTorque(*v, ToForceMode(mode));
	}
	float RB_GetFloat(uint64 id, int prop)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (rb == nullptr) return 0.0f;
		return prop == 0 ? rb->GetMass() : (prop == 1 ? rb->GetLinearDamping() : rb->GetAngularDamping());
	}
	void RB_SetFloat(uint64 id, int prop, float v)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (rb == nullptr) return;
		if (prop == 0) rb->SetMass(v);
		else if (prop == 1) rb->SetLinearDamping(v);
		else rb->SetAngularDamping(v);
	}
	int RB_GetBool(uint64 id, int prop)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (rb == nullptr) return 0;
		return prop == 0 ? rb->GetUseGravity() : (prop == 1 ? rb->IsKinematic() : rb->IsSleeping());
	}
	void RB_SetBool(uint64 id, int prop, int v)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (rb == nullptr) return;
		if (prop == 0) rb->SetUseGravity(v != 0);
		else if (prop == 1) rb->SetKinematic(v != 0);
	}
	void RB_Move(uint64 id, int kind, Vec4* v)
	{
		RigidBody* rb = Get<RigidBody>(id);
		if (rb == nullptr || v == nullptr) return;
		if (kind == 0) rb->MovePosition(Vec3(v->x, v->y, v->z));
		else rb->MoveRotation(Quaternion(v->x, v->y, v->z, v->w));
	}

	// Character Controller — float: 0 slopeLimit, 1 stepOffset, 2 skinWidth, 3 minMoveDistance, 4 radius, 5 height
	//                         vector: 0 velocity, 1 center / int: 0 collisionFlags, 1 isGrounded, 2 detectCollisions
	int CC_Move(uint64 id, Vec3* v, float dt, int simple)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (cc == nullptr || v == nullptr) return 0;
		if (simple) return cc->SimpleMove(*v, dt) ? 1 : 0;
		return cc->Move(*v, dt);
	}
	float CC_GetFloat(uint64 id, int prop)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (cc == nullptr) return 0.0f;
		switch (prop) { case 0: return cc->GetSlopeLimit(); case 1: return cc->GetStepOffset(); case 2: return cc->GetSkinWidth(); case 3: return cc->GetMinMoveDistance(); case 4: return cc->GetRadius(); default: return cc->GetHeight(); }
	}
	void CC_SetFloat(uint64 id, int prop, float v)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (cc == nullptr) return;
		switch (prop) { case 0: cc->SetSlopeLimit(v); break; case 1: cc->SetStepOffset(v); break; case 2: cc->SetSkinWidth(v); break; case 3: cc->SetMinMoveDistance(v); break; case 4: cc->SetRadius(v); break; default: cc->SetHeight(v); break; }
	}
	void CC_GetVector(uint64 id, int prop, Vec3* out)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (out == nullptr) return;
		*out = cc == nullptr ? Vec3::Zero : (prop == 0 ? cc->GetVelocity() : cc->GetCenter());
	}
	void CC_SetVector(uint64 id, int prop, Vec3* v)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (cc != nullptr && v != nullptr && prop == 1) cc->SetCenter(*v);
	}
	int CC_GetInt(uint64 id, int prop)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (cc == nullptr) return 0;
		return prop == 0 ? cc->GetCollisionFlags() : (prop == 1 ? (int)cc->IsGrounded() : (int)cc->GetDetectCollisions());
	}
	void CC_SetInt(uint64 id, int prop, int v)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (cc != nullptr && prop == 2) cc->SetDetectCollisions(v != 0);
	}
	int CC_GetHit(uint64 id, int index, ControllerHitData* out)
	{
		CharacterController* cc = Get<CharacterController>(id);
		if (cc == nullptr || out == nullptr || index < 0 || index >= (int)cc->GetHits().size()) return 0;
		const ControllerColliderHit& h = cc->GetHits()[index];
		out->point = h.point;
		out->normal = h.normal;
		out->moveDirection = h.moveDirection;
		out->moveLength = h.moveLength;
		out->gameObject = h.gameObject ? h.gameObject->GetFileID() : 0;
		return 1;
	}

	// Joint — float: 0 breakForce, 1 breakTorque, 2 enableCollision(0/1), 3 autoConfigureConnectedAnchor(0/1),
	//   Hinge: 10 useSpring, 11 spring, 12 damper, 13 targetPosition, 14 useMotor, 15 targetVelocity, 16 force, 17 freeSpin,
	//          18 useLimits, 19 min, 20 max, 21 bounciness, 30 angle(읽기), 31 velocity(읽기)
	//   Spring: 40 spring, 41 damper, 42 minDistance, 43 maxDistance
	//   Character: 50 · 51 twistLimitSpring, 52~54 lowTwistLimit (limit, bounciness, contactDistance), 55~57 highTwistLimit,
	//              58 · 59 swingLimitSpring, 60~62 swing1Limit, 63~65 swing2Limit, 66 enableProjection, 67 projectionDistance, 68 projectionAngle
	//   Configurable: 100~105 motion (x, y, z, angularX, angularY, angularZ), 106 · 107 linearLimitSpring, 108~110 linearLimit,
	//              111 · 112 angularXLimitSpring, 113~115 lowAngularXLimit, 116~118 highAngularXLimit, 119 · 120 angularYZLimitSpring,
	//              121~123 angularYLimit, 124~126 angularZLimit, 130 + 3 × d + (0 positionSpring, 1 positionDamper, 2 maximumForce)
	//              (d = 0 x, 1 y, 2 z, 3 angularX, 4 angularYZ, 5 slerp), 150 rotationDriveMode, 160~163 targetRotation (x, y, z, w)
	//   vector: 0 anchor, 1 axis, 2 connectedAnchor, 3 swingAxis · secondaryAxis, 4 targetPosition, 5 targetVelocity, 6 targetAngularVelocity
	Joint* FindJoint(uint64 id, int kind)
	{
		GameObject* g = Find(id);
		if (g == nullptr) return nullptr;
		auto pick = [&](const std::vector<std::shared_ptr<Component>>& list) -> Joint* {
			for (const auto& c : list)
				if (auto* j = dynamic_cast<Joint*>(c.get()))
					if (j->JointKind() == kind) return j;
			return nullptr;
		};
		Joint* j = pick(g->GetComponents());
		return j ? j : pick(g->GetPendingComponents());
	}
	// WheelCollider 의 float 칸 (바인딩 표 주석 참고)
	float* WheelFloat(WheelCollider* w, int p)
	{
		auto curve = [](WheelFrictionCurveData& c, int i) { float* f[5] = { &c.ExtremumSlip, &c.ExtremumValue, &c.AsymptoteSlip, &c.AsymptoteValue, &c.Stiffness }; return f[i]; };
		switch (p)
		{
		case 0: return &w->Mass; case 1: return &w->Radius; case 2: return &w->WheelDampingRate; case 3: return &w->SuspensionDistance;
		case 4: return &w->ForceAppPointDistance; case 5: return &w->SuspensionSpring.Spring; case 6: return &w->SuspensionSpring.Damper;
		case 7: return &w->SuspensionSpring.TargetPosition; case 30: return &w->MotorTorque; case 31: return &w->BrakeTorque; case 32: return &w->SteerAngle;
		}
		if (p >= 10 && p <= 14) return curve(w->ForwardFriction, p - 10);
		if (p >= 20 && p <= 24) return curve(w->SidewaysFriction, p - 20);
		return nullptr;
	}

	// Character · Configurable 의 float 칸 (정수 · 참거짓 칸은 따로)
	float* JointFloatField(Joint* j, int prop)
	{
		auto lim = [](SoftJointLimitData& l, int i) { return i == 0 ? &l.Limit : (i == 1 ? &l.Bounciness : &l.ContactDistance); };
		auto spr = [](SoftJointLimitSpringData& s, int i) { return i == 0 ? &s.Spring : &s.Damper; };
		if (auto* c = dynamic_cast<CharacterJoint*>(j))
		{
			if (prop == 50 || prop == 51) return spr(c->TwistLimitSpring, prop - 50);
			if (prop >= 52 && prop <= 54) return lim(c->LowTwistLimit, prop - 52);
			if (prop >= 55 && prop <= 57) return lim(c->HighTwistLimit, prop - 55);
			if (prop == 58 || prop == 59) return spr(c->SwingLimitSpring, prop - 58);
			if (prop >= 60 && prop <= 62) return lim(c->Swing1Limit, prop - 60);
			if (prop >= 63 && prop <= 65) return lim(c->Swing2Limit, prop - 63);
			if (prop == 67) return &c->ProjectionDistance;
			if (prop == 68) return &c->ProjectionAngle;
		}
		if (auto* c = dynamic_cast<ConfigurableJoint*>(j))
		{
			if (prop == 106 || prop == 107) return spr(c->LinearLimitSpring, prop - 106);
			if (prop >= 108 && prop <= 110) return lim(c->LinearLimit, prop - 108);
			if (prop == 111 || prop == 112) return spr(c->AngularXLimitSpring, prop - 111);
			if (prop >= 113 && prop <= 115) return lim(c->LowAngularXLimit, prop - 113);
			if (prop >= 116 && prop <= 118) return lim(c->HighAngularXLimit, prop - 116);
			if (prop == 119 || prop == 120) return spr(c->AngularYZLimitSpring, prop - 119);
			if (prop >= 121 && prop <= 123) return lim(c->AngularYLimit, prop - 121);
			if (prop >= 124 && prop <= 126) return lim(c->AngularZLimit, prop - 124);
			if (prop >= 130 && prop < 148)
			{
				JointDriveData* drives[6] = { &c->XDrive, &c->YDrive, &c->ZDrive, &c->AngularXDrive, &c->AngularYZDrive, &c->SlerpDrive };
				JointDriveData& d = *drives[(prop - 130) / 3];
				const int f = (prop - 130) % 3;
				return f == 0 ? &d.PositionSpring : (f == 1 ? &d.PositionDamper : &d.MaximumForce);
			}
			if (prop >= 160 && prop <= 163) return prop == 160 ? &c->TargetRotation.x : (prop == 161 ? &c->TargetRotation.y : (prop == 162 ? &c->TargetRotation.z : &c->TargetRotation.w));
		}
		return nullptr;
	}
	int* ConfigurableIntField(Joint* j, int prop)
	{
		auto* c = dynamic_cast<ConfigurableJoint*>(j);
		if (c == nullptr) return nullptr;
		int* motions[6] = { &c->XMotion, &c->YMotion, &c->ZMotion, &c->AngularXMotion, &c->AngularYMotion, &c->AngularZMotion };
		if (prop >= 100 && prop <= 105) return motions[prop - 100];
		if (prop == 150) return &c->RotationDriveMode;
		return nullptr;
	}
	Vec3* JointVectorField(Joint* j, int prop)
	{
		if (auto* c = dynamic_cast<CharacterJoint*>(j))
			return prop == 3 ? &c->SwingAxis : nullptr;
		if (auto* c = dynamic_cast<ConfigurableJoint*>(j))
			switch (prop) { case 3: return &c->SecondaryAxis; case 4: return &c->TargetPosition; case 5: return &c->TargetVelocity; case 6: return &c->TargetAngularVelocity; }
		return nullptr;
	}

	float JT_GetFloat(uint64 id, int kind, int prop)
	{
		Joint* j = FindJoint(id, kind);
		if (j == nullptr) return 0.0f;
		auto* h = dynamic_cast<HingeJoint*>(j);
		auto* s = dynamic_cast<SpringJoint*>(j);
		switch (prop)
		{
		case 0: return j->GetBreakForce();
		case 1: return j->GetBreakTorque();
		case 2: return j->GetEnableCollision() ? 1.0f : 0.0f;
		case 3: return j->GetAutoConfigureConnectedAnchor() ? 1.0f : 0.0f;
		}
		if (h)
			switch (prop)
			{
			case 10: return h->UseSpring; case 11: return h->Spring.Spring; case 12: return h->Spring.Damper; case 13: return h->Spring.TargetPosition;
			case 14: return h->UseMotor; case 15: return h->Motor.TargetVelocity; case 16: return h->Motor.Force; case 17: return h->Motor.FreeSpin;
			case 18: return h->UseLimits; case 19: return h->Limits.Min; case 20: return h->Limits.Max; case 21: return h->Limits.Bounciness;
			case 30: return h->GetAngle(); case 31: return h->GetVelocity();
			}
		if (s)
			switch (prop) { case 40: return s->SpringValue; case 41: return s->Damper; case 42: return s->MinDistance; case 43: return s->MaxDistance; }
		if (float* f = JointFloatField(j, prop)) return *f;
		if (int* i = ConfigurableIntField(j, prop)) return (float)*i;
		if (auto* c = dynamic_cast<CharacterJoint*>(j); c && prop == 66) return c->EnableProjection ? 1.0f : 0.0f;
		return 0.0f;
	}
	void JT_SetFloat(uint64 id, int kind, int prop, float v)
	{
		Joint* j = FindJoint(id, kind);
		if (j == nullptr) return;
		auto* h = dynamic_cast<HingeJoint*>(j);
		auto* s = dynamic_cast<SpringJoint*>(j);
		const bool b = v != 0.0f;
		switch (prop)
		{
		case 0: j->SetBreakForce(v); return;
		case 1: j->SetBreakTorque(v); return;
		case 2: j->SetEnableCollision(b); return;
		case 3: j->SetAutoConfigureConnectedAnchor(b); return;
		}
		if (h)
			switch (prop)
			{
			case 10: h->UseSpring = b; return; case 11: h->Spring.Spring = v; return; case 12: h->Spring.Damper = v; return; case 13: h->Spring.TargetPosition = v; return;
			case 14: h->UseMotor = b; return; case 15: h->Motor.TargetVelocity = v; return; case 16: h->Motor.Force = v; return; case 17: h->Motor.FreeSpin = b; return;
			case 18: h->UseLimits = b; return; case 19: h->Limits.Min = v; return; case 20: h->Limits.Max = v; return; case 21: h->Limits.Bounciness = v; return;
			}
		if (s)
			switch (prop) { case 40: s->SpringValue = v; return; case 41: s->Damper = v; return; case 42: s->MinDistance = v; return; case 43: s->MaxDistance = v; return; }
		if (float* f = JointFloatField(j, prop)) { *f = v; return; }
		if (int* i = ConfigurableIntField(j, prop)) { *i = std::clamp((int)v, 0, prop == 150 ? 1 : 2); return; }
		if (auto* c = dynamic_cast<CharacterJoint*>(j); c && prop == 66) c->EnableProjection = b;
	}
	void JT_GetVector(uint64 id, int kind, int prop, Vec3* out)
	{
		Joint* j = FindJoint(id, kind);
		if (out == nullptr) return;
		if (Vec3* f = j ? JointVectorField(j, prop) : nullptr) { *out = *f; return; }
		*out = j == nullptr || prop > 2 ? Vec3::Zero : (prop == 0 ? j->GetAnchor() : (prop == 1 ? j->GetAxis() : j->GetConnectedAnchor()));
	}
	void JT_SetVector(uint64 id, int kind, int prop, Vec3* v)
	{
		Joint* j = FindJoint(id, kind);
		if (j == nullptr || v == nullptr) return;
		if (Vec3* f = JointVectorField(j, prop)) { *f = *v; return; }
		if (prop == 0) j->SetAnchor(*v); else if (prop == 1) j->SetAxis(*v); else if (prop == 2) j->SetConnectedAnchor(*v);
	}
	uint64 JT_GetConnected(uint64 id, int kind) { Joint* j = FindJoint(id, kind); return j ? j->GetConnectedBody() : 0; }
	void JT_SetConnected(uint64 id, int kind, uint64 other) { if (Joint* j = FindJoint(id, kind)) j->SetConnectedBody(other); }

	void AS_Call(uint64 id, int op)
	{
		AudioSource* a = Get<AudioSource>(id);
		if (a == nullptr) return;
		switch (op) { case 0: a->Play(); break; case 1: a->Stop(); break; case 2: a->Pause(); break; default: a->UnPause(); break; }
	}
	float AS_GetFloat(uint64 id, int prop)
	{
		AudioSource* a = Get<AudioSource>(id);
		if (a == nullptr) return 0.0f;
		switch (prop)
		{
		case 0: return a->GetVolume(); case 1: return a->GetPitch(); case 2: return a->GetStereoPan(); case 3: return a->GetSpatialBlend();
		case 5: return a->GetDopplerLevel(); case 6: return a->GetSpread(); case 7: return a->GetMinDistance(); case 8: return a->GetMaxDistance();
		default: return a->GetTime();
		}
	}
	void AS_SetFloat(uint64 id, int prop, float v)
	{
		AudioSource* a = Get<AudioSource>(id);
		if (a == nullptr) return;
		switch (prop)
		{
		case 0: a->SetVolume(v); break; case 1: a->SetPitch(v); break; case 2: a->SetStereoPan(v); break; case 3: a->SetSpatialBlend(v); break;
		case 5: a->SetDopplerLevel(v); break; case 6: a->SetSpread(v); break;
		case 7: a->SetDistances(v, a->GetMaxDistance()); break; case 8: a->SetDistances(a->GetMinDistance(), v); break;
		default: break;
		}
	}
	int AS_GetBool(uint64 id, int prop)
	{
		AudioSource* a = Get<AudioSource>(id);
		if (a == nullptr) return 0;
		switch (prop) { case 0: return a->IsPlaying(); case 1: return a->GetLoop(); case 2: return a->GetMute(); default: return a->GetPlayOnAwake(); }
	}
	void AS_SetBool(uint64 id, int prop, int v)
	{
		AudioSource* a = Get<AudioSource>(id);
		if (a == nullptr) return;
		switch (prop) { case 1: a->SetLoop(v != 0); break; case 2: a->SetMute(v != 0); break; case 3: a->SetPlayOnAwake(v != 0); break; default: break; }
	}
	void AS_PlayOneShot(uint64 id, u8* clipPath, float volume)
	{
		auto clip = clipPath ? AudioClip::Load(clipPath) : nullptr;
		if (clip == nullptr) return;
		if (id == 0) { AudioManager::PlayOneShot(clip, volume); return; }
		if (AudioSource* a = Get<AudioSource>(id)) a->PlayOneShot(clip, volume);
	}
	u8* AS_GetClip(uint64 id) { AudioSource* a = Get<AudioSource>(id); return Ret(a ? a->GetClipPath() : std::string()); }
	void AS_SetClip(uint64 id, u8* path) { if (AudioSource* a = Get<AudioSource>(id)) a->SetClip(path ? path : ""); }
	float Audio_ClipLength(u8* path) { auto clip = path ? AudioClip::Load(path) : nullptr; return clip ? clip->Length : 0.0f; }

	// ---- AudioSource.outputAudioMixerGroup ("믹서 경로|그룹", 빈 문자열 = 없음)
	u8* AS_GetOutput(uint64 id)
	{
		AudioSource* a = Get<AudioSource>(id);
		if (a == nullptr || a->GetOutputMixer().empty()) return Ret(std::string());
		return Ret(a->GetOutputMixer() + "|" + a->GetOutputGroup());
	}
	void AS_SetOutput(uint64 id, u8* mixer, u8* group)
	{
		if (AudioSource* a = Get<AudioSource>(id))
			a->SetOutput(mixer ? mixer : "", group ? group : "");
	}

	// ---- AudioMixer (경로로 찾는다 — 같은 경로 = 같은 믹서)
	int MX_Load(u8* path) { return path && AudioMixer::Load(path) ? 1 : 0; }
	int MX_SetFloat(u8* path, u8* name, float v) { auto m = path ? AudioMixer::Load(path) : nullptr; return m && name && m->SetFloat(name, v) ? 1 : 0; }
	int MX_GetFloat(u8* path, u8* name, float* out)
	{
		auto m = path ? AudioMixer::Load(path) : nullptr;
		float v = 0.0f;
		const bool ok = m && name && m->GetFloat(name, v);
		if (out) *out = v;
		return ok ? 1 : 0;
	}
	int MX_ClearFloat(u8* path, u8* name) { auto m = path ? AudioMixer::Load(path) : nullptr; return m && name && m->ClearFloat(name) ? 1 : 0; }
	int MX_Transition(u8* path, u8* snapshot, float seconds) { auto m = path ? AudioMixer::Load(path) : nullptr; return m && snapshot && m->TransitionTo(snapshot, seconds) ? 1 : 0; }
	// 이름 목록 (줄바꿈으로): 0 그룹, 1 스냅숏, 2 노출 파라미터
	u8* MX_Names(u8* path, int what)
	{
		auto m = path ? AudioMixer::Load(path) : nullptr;
		std::string s;
		if (m)
		{
			if (what == 0) for (const auto& g : m->Groups) s += g.Name + "\n";
			else if (what == 1) for (const auto& n : m->Snapshots) s += n.Name + "\n";
			else for (const auto& e : m->ExposedParams) s += e.Name + "\n";
		}
		return Ret(s);
	}
	float MX_GroupLevel(u8* path, u8* group)
	{
		auto m = path ? AudioMixer::Load(path) : nullptr;
		return m && group ? m->GroupLevelDb(m->FindGroup(group)) : -80.0f;
	}

	int PH_Raycast(Vec3* origin, Vec3* dir, float maxDistance, RaycastData* out)
	{
		if (origin == nullptr || dir == nullptr || out == nullptr) return 0;
		*out = RaycastData{};
		Vec3 d = *dir;
		if (d.LengthSquared() < 1e-12f) return 0;
		d.Normalize();
		RaycastHit hit;
		if (!PhysicsManager::GetI()->Raycast(*origin, d, hit, maxDistance, false))
			return 0;
		out->point = hit.point;
		out->normal = hit.normal;
		out->distance = hit.distance;
		out->gameObject = hit.gameObject ? hit.gameObject->GetFileID() : 0;
		return 1;
	}

	uint64 Camera_Main()
	{
		auto cam = DisplayManager::GetI()->GetActiveCamera();
		return cam && cam->GetGameObject() ? cam->GetGameObject()->GetFileID() : 0;
	}

	void Script_SetEnabled(void* component, int enabled)
	{
		if (component)
			static_cast<CSharpScript*>(component)->SetEnabledFromScript(enabled != 0);
	}

	// ---------------------------------------------------------------- 씬 / 앱
	// 비교용: 구분자를 '/' 로 (안드로이드 · 웹은 '\' 를 경로 구분자로 보지 않아 stem() 이 파일 이름을 못 뗀다 — Windows 는 둘 다)
	std::string NormalizeScenePath(std::string s)
	{
		std::replace(s.begin(), s.end(), '\\', '/');
		return s;
	}

	// Build Settings 의 씬: Unity 처럼 이름("Level2"), 경로("Assets/Scenes/Level2.unity"), 확장자 없는 경로, 빌드 번호
	std::string FindBuildScene(u8* name, int index)
	{
		const std::vector<std::string> scenes = BuildSettings::RuntimeScenes();
		std::string path;
		if (name != nullptr)
		{
			// Unity: 이름("Level2"), 경로("Assets/Scenes/Level2.unity"), 확장자 없는 경로 모두 받는다
			const std::string want = NormalizeScenePath(name);
			for (const std::string& s : scenes)
			{
				const std::string norm = NormalizeScenePath(s);
				const std::string stem = std::filesystem::path(norm).stem().string();
				const std::string noExt = norm.size() > 6 ? norm.substr(0, norm.size() - 6) : norm;
				if (_stricmp(norm.c_str(), want.c_str()) == 0 || _stricmp(stem.c_str(), want.c_str()) == 0 || _stricmp(noExt.c_str(), want.c_str()) == 0)
				{
					path = s;
					break;
				}
			}
		}
		else if (index >= 0 && index < (int)scenes.size())
			path = scenes[index];
		return path;
	}

	int Scene_Load(u8* name, int index)
	{
		const std::string path = FindBuildScene(name, index);
		if (path.empty())
			return 0;
		if (Application::IsPlaying())
			SceneManager::GetI()->LoadSceneDuringPlay(string_to_wstring(path));
		return 1;
	}

	u8* Scene_Active(int* buildIndex)
	{
		Scene* scene = CurrentScene();
		const std::string path = scene ? wstring_to_string(scene->GetScenePath()) : std::string();
		if (buildIndex)
		{
			*buildIndex = -1;
			const std::vector<std::string> scenes = BuildSettings::RuntimeScenes();
			for (int i = 0; i < (int)scenes.size(); ++i)
				if (_stricmp(NormalizeScenePath(scenes[i]).c_str(), NormalizeScenePath(path).c_str()) == 0)
					*buildIndex = i;
		}
		return Ret(path);
	}

	u8* Scene_PathAt(int index)
	{
		const std::vector<std::string> scenes = BuildSettings::RuntimeScenes();
		return index >= 0 && index < (int)scenes.size() ? Ret(scenes[index]) : nullptr;
	}

	int Scene_Count() { return (int)BuildSettings::RuntimeScenes().size(); }

	int Scene_LoadOp(u8* name, int index, int mode, int async)
	{
		const std::string path = FindBuildScene(name, index);
		if (path.empty())
			return 0;
		if (!Application::IsPlaying())
			return -1;
		return SceneManager::GetI()->RequestSceneLoad(string_to_wstring(path), mode == 1, async != 0);
	}

	int Scene_OpState(int op, float* progress, int* handle)
	{
		const SceneManager::SceneOp* s = SceneManager::GetI()->GetSceneOp(op);
		if (s == nullptr)
			return -1;
		if (progress) *progress = s->Progress;
		if (handle) *handle = s->Handle;
		return (s->Done ? 1 : 0) | (s->Failed ? 2 : 0) | (s->AllowActivation ? 4 : 0);
	}

	u8* Scene_HandleInfo(int handle, int* loaded, int* rootCount)
	{
		SceneManager* sm = SceneManager::GetI();
		const std::wstring path = sm->ScenePathOfHandle(handle);
		bool isLoaded = false;
		for (const SceneManager::RuntimeScene& s : sm->LoadedScenes())
			if (s.Handle == handle) isLoaded = true;
		if (!isLoaded && handle == SceneManager::kDontDestroyOnLoadHandle)
			isLoaded = Application::IsPlaying();
		if (loaded) *loaded = isLoaded ? 1 : 0;
		if (rootCount) *rootCount = (int)sm->RootsOfScene(handle).size();
		return Ret(wstring_to_string(path));
	}

	int Scene_Roots(int handle, uint64* out, int max)
	{
		const std::vector<GameObject*> roots = SceneManager::GetI()->RootsOfScene(handle);
		for (int i = 0; i < (int)roots.size() && i < max; ++i)
			out[i] = roots[i]->GetFileID();
		return (int)roots.size();
	}
	int App_Info(int what)
	{
		if (what == 0) return Application::IsPlayer() ? 1 : 0;
		if (what == 1) return Application::IsPlaying() ? 1 : 0;
		return 0;
	}
	u8* App_ProductName() { return Ret(BuildSettings::ProductName()); }

	// ---------------------------------------------------------------- Profiler
	int Prof_Begin(const char* name, int64_t* startNs, uint16_t* depth)
	{
		if (!Profiler::Collecting())
			return 0;
		if (Profiler::IsMainThread())
		{
			Profiler::Begin(name);
			return 1;
		}
		Profiler::ThreadSampleBegin(*startNs, *depth);
		return 2;
	}
	void Prof_End(int mode, const char* name, int64_t startNs, uint16_t depth)
	{
		if (mode == 1)
			Profiler::End();
		else if (mode == 2)
			Profiler::ThreadSampleEnd(name, startNs, depth);
	}
	void Prof_SetThreadName(const char* name) { Profiler::SetThreadName(name); }
	void App_Quit()
	{
		if (Application::IsPlayer())
			PlayerRuntime::Quit();
	}

	// ---------------------------------------------------------------- Particle System
	// 번호표는 ScriptCore/Interop/NativeApi.cs 주석과 같다
	ParticleSystem* PS(uint64 id)
	{
		GameObject* g = Find(id);
		return g ? g->GetComponentIncludingPending<ParticleSystem>() : nullptr;   // AddComponent 직후에도
	}

	void PS_Call(uint64 id, int op, int arg)
	{
		ParticleSystem* ps = PS(id);
		if (ps == nullptr) return;
		switch (op)
		{
		case 0: ps->Play(arg != 0); break;
		case 1: ps->Stop((arg & 1) != 0, (arg & 2) != 0); break;
		case 2: ps->Pause(arg != 0); break;
		case 3: ps->Clear(arg != 0); break;
		case 4: ps->Emit(arg); break;
		default: ps->Restart(); break;
		}
	}

	MinMaxCurve* PSCurve(ParticleSystem* ps, int prop)
	{
		switch (prop)
		{
		case 0: return &ps->StartDelay;
		case 1: return &ps->StartLifetime;
		case 2: return &ps->StartSpeed;
		case 3: return &ps->StartSize;
		case 4: return &ps->StartRotation;
		case 5: return &ps->GravityModifier;
		case 6: return &ps->RateOverTime;
		case 7: return &ps->RateOverDistance;
		default: return nullptr;
		}
	}

	float PS_GetFloat(uint64 id, int prop)
	{
		ParticleSystem* ps = PS(id);
		if (ps == nullptr) return 0.0f;
		switch (prop)
		{
		case 0: return ps->GetTime();
		case 1: return (float)ps->ParticleCount();
		case 2: return ps->Duration;
		case 3: return ps->SimulationSpeed;
		case 4: return (float)ps->MaxParticles;
		case 5: return ps->IsPlaying() ? 1.0f : 0.0f;
		case 6: return ps->IsPaused() ? 1.0f : 0.0f;
		case 7: return ps->IsStopped() ? 1.0f : 0.0f;
		case 8: return ps->IsEmitting() ? 1.0f : 0.0f;
		case 9: return ps->Looping ? 1.0f : 0.0f;
		case 10: return ps->PlayOnAwake ? 1.0f : 0.0f;
		case 11: return (float)ps->SimulationSpace;
		case 12: return ps->EmissionEnabled ? 1.0f : 0.0f;
		case 13: return ps->ShapeEnabled ? 1.0f : 0.0f;
		case 14: return ps->ShapeRadius;
		case 15: return ps->ShapeAngle;
		case 16: return (float)(int)ps->Shape;
		case 17:
		{
			std::vector<ParticleSystem*> group;
			ps->CollectHierarchy(group);
			for (ParticleSystem* p : group)
				if (p->IsAlive()) return 1.0f;
			return 0.0f;
		}
		case 18: return ps->ShapeArc;
		case 19: return ps->ColorEnabled ? 1.0f : 0.0f;
		case 20: return ps->IsAlive() ? 1.0f : 0.0f;
		case 21: return ps->TrailsEnabled ? 1.0f : 0.0f;
		case 22: return ps->CollisionEnabled ? 1.0f : 0.0f;
		case 23: return ps->SubEmittersEnabled ? 1.0f : 0.0f;
		default: return 0.0f;
		}
	}

	void PS_SetFloat(uint64 id, int prop, float v)
	{
		ParticleSystem* ps = PS(id);
		if (ps == nullptr) return;
		switch (prop)
		{
		case 0: ps->SetTime(v); break;
		case 2: ps->Duration = (std::max)(0.05f, v); break;
		case 3: ps->SimulationSpeed = (std::max)(0.0f, v); break;
		case 4: ps->MaxParticles = (std::max)(0, (int)v); break;
		case 9: ps->Looping = v != 0.0f; break;
		case 10: ps->PlayOnAwake = v != 0.0f; break;
		case 11: ps->SimulationSpace = v != 0.0f ? 1 : 0; break;
		case 12: ps->EmissionEnabled = v != 0.0f; break;
		case 13: ps->ShapeEnabled = v != 0.0f; break;
		case 14: ps->ShapeRadius = (std::max)(0.0001f, v); break;
		case 15: ps->ShapeAngle = std::clamp(v, 0.0f, 90.0f); break;
		case 16: ps->Shape = (ParticleSystem::ShapeType)std::clamp((int)v, 0, 5); break;
		case 18: ps->ShapeArc = std::clamp(v, 0.0f, 360.0f); break;
		case 19: ps->ColorEnabled = v != 0.0f; break;
		case 21: ps->TrailsEnabled = v != 0.0f; break;
		case 22: ps->CollisionEnabled = v != 0.0f; break;
		case 23: ps->SubEmittersEnabled = v != 0.0f; break;
		default: break;
		}
	}

	void PS_GetCurve(uint64 id, int prop, Vec4* out)
	{
		ParticleSystem* ps = PS(id);
		MinMaxCurve* c = ps ? PSCurve(ps, prop) : nullptr;
		if (c == nullptr || out == nullptr) return;
		*out = Vec4((float)(int)c->Mode, c->ConstantMin, c->ConstantMax, c->Multiplier);
	}

	void PS_SetCurve(uint64 id, int prop, int mode, float minV, float maxV)
	{
		ParticleSystem* ps = PS(id);
		MinMaxCurve* c = ps ? PSCurve(ps, prop) : nullptr;
		if (c == nullptr) return;
		// 스크립트에서는 상수 모드만 (곡선은 Inspector 에서)
		c->Mode = mode == (int)ParticleCurveMode::TwoConstants ? ParticleCurveMode::TwoConstants : ParticleCurveMode::Constant;
		c->ConstantMin = minV;
		c->ConstantMax = maxV;
	}

	MinMaxGradient* PSColor(ParticleSystem* ps, int prop)
	{
		return prop == 0 ? &ps->StartColor : prop == 1 ? &ps->ColorOverLifetime : nullptr;
	}

	int PS_GetColor(uint64 id, int prop, Vec4* colorMin, Vec4* colorMax)
	{
		ParticleSystem* ps = PS(id);
		MinMaxGradient* g = ps ? PSColor(ps, prop) : nullptr;
		if (g == nullptr) return 0;
		if (colorMin) *colorMin = g->ColorMin;
		if (colorMax) *colorMax = g->ColorMax;
		return (int)g->Mode;
	}

	void PS_SetColor(uint64 id, int prop, int mode, Vec4* colorMin, Vec4* colorMax)
	{
		ParticleSystem* ps = PS(id);
		MinMaxGradient* g = ps ? PSColor(ps, prop) : nullptr;
		if (g == nullptr) return;
		if (colorMin) g->ColorMin = *colorMin;
		if (colorMax) g->ColorMax = *colorMax;
		if (prop == 1)
		{
			// Color over Lifetime 은 그라디언트: 한 색이면 그 색에서 투명으로 사라지게
			const Vec4 c = g->ColorMax;
			g->Mode = ParticleGradientMode::Gradient;
			g->GradientMax.Colors = { { 0, c.x, c.y, c.z }, { 1, c.x, c.y, c.z } };
			g->GradientMax.Alphas = { { 0, c.w }, { 1, 0.0f } };
			return;
		}
		g->Mode = mode == (int)ParticleGradientMode::TwoColors ? ParticleGradientMode::TwoColors : ParticleGradientMode::Color;
	}

	// ---------------------------------------------------------------- C# Job System 과 엔진 (TransformAccessArray · RaycastCommand)
	// TransformAccessArray: 예약할 때 메인 스레드가 모으고 (TR_Gather) 작업 스레드가 사본을 고친 뒤 Complete 때 되쓴다 (TR_Scatter) — 엔진 Transform 은 메인만 만진다
	struct TransformAccessData   // ScriptCore/Engine/Jobs/EngineJobs.cs 와 같은 배치
	{
		Vec3 position;
		Quaternion rotation;
		Vec3 localPosition;
		Quaternion localRotation;
		Vec3 localScale;
		Vec3 lossyScale;
		int flags;
		int pad;
	};
	static_assert(sizeof(TransformAccessData) == 88, "TransformAccessData layout must match C#");
	enum : int { kTAValid = 1, kTAPosition = 2, kTARotation = 4, kTALocalPosition = 8, kTALocalRotation = 16, kTALocalScale = 32 };

	int TR_Gather(const uint64* ids, int count, TransformAccessData* out)
	{
		if (ids == nullptr || out == nullptr) return 0;
		int valid = 0;
		for (int i = 0; i < count; ++i)
		{
			TransformAccessData& d = out[i];
			GameObject* g = Find(ids[i]);
			if (g == nullptr)
			{
				d = TransformAccessData{};
				d.rotation = d.localRotation = Quaternion::Identity;
				d.localScale = d.lossyScale = Vec3::One;
				continue;
			}
			Transform* t = g->GetTransform();
			d.position = t->GetPosition();
			d.rotation = t->GetRotation();
			d.localPosition = t->GetLocalPosition();
			d.localRotation = t->GetLocalRotation();
			d.localScale = t->GetLocalScale();
			d.lossyScale = t->GetScale();
			d.flags = kTAValid;
			d.pad = 0;
			++valid;
		}
		return valid;
	}

	// 잡이 바꾼 값만 (로컬 먼저, 월드가 이긴다)
	int TR_Scatter(const uint64* ids, int count, const TransformAccessData* in)
	{
		if (ids == nullptr || in == nullptr) return 0;
		int written = 0;
		for (int i = 0; i < count; ++i)
		{
			const TransformAccessData& d = in[i];
			const int f = d.flags;
			if ((f & kTAValid) == 0 || (f & ~kTAValid) == 0) continue;
			GameObject* g = Find(ids[i]);
			if (g == nullptr) continue;
			Transform* t = g->GetTransform();
			if (f & kTALocalScale) t->SetLocalScale(d.localScale);
			if (f & kTALocalRotation) t->SetLocalRotation(d.localRotation);
			if (f & kTALocalPosition) t->SetLocalPosition(d.localPosition);
			if (f & kTARotation) t->SetRotation(d.rotation);
			if (f & kTAPosition) t->SetPosition(d.position);
			++written;
		}
		return written;
	}

	// RaycastCommand (C# 와 같은 48 바이트) — 메인 스레드가 부르면 렌더링과 겹친 물리 스텝을 끝내고 엔진 Job System 으로 나눠 쏜다 (네이티브, 병렬)
	struct RaycastCommandData
	{
		Vec3 from;
		Vec3 direction;
		int layerMask;
		uint8_t hitMultipleFaces, pad0[3];
		int hitTriggers;          // QueryTriggerInteraction: 0 UseGlobal (= Ignore, C# Physics.Raycast 와 같다), 1 Ignore, 2 Collide
		uint8_t hitBackfaces, pad1[3];
		float distance;
		int physicsScene;
	};
	static_assert(sizeof(RaycastCommandData) == 48, "RaycastCommand layout must match C#");
	static_assert(sizeof(RaycastData) == 40, "RaycastHit layout must match C#");

	int PH_RaycastBatch(const RaycastCommandData* cmds, int count, RaycastData* results, int maxHits)
	{
		if (cmds == nullptr || results == nullptr || count <= 0) return 0;
		maxHits = (std::max)(1, maxHits);
		PhysicsManager* pm = PhysicsManager::GetI();
		pm->CompleteAsync();   // 그 뒤 일꾼은 물리 월드를 읽기만 한다 (메인은 ParallelFor 가 끝날 때까지 기다린다)
		std::atomic<int> hits{ 0 };
		Jobs::ParallelFor(count, 16, [&](int begin, int end) {
			std::vector<RaycastHit> all;
			for (int i = begin; i < end; ++i)
			{
				const RaycastCommandData& c = cmds[i];
				RaycastData* out = results + (size_t)i * maxHits;
				for (int k = 0; k < maxHits; ++k) out[k] = RaycastData{};
				Vec3 dir = c.direction;
				const float len = dir.Length();
				if (len < 1e-6f || !(c.distance > 0.0f)) continue;
				dir /= len;
				const float dist = (std::min)(c.distance, 1.0e6f);   // Unity 기본 float.MaxValue
				const bool triggers = c.hitTriggers == 2;
				if (maxHits == 1)
				{
					RaycastHit h;
					if (!pm->Raycast(c.from, dir, h, dist, triggers, (uint32)c.layerMask)) continue;
					out[0].point = h.point;
					out[0].normal = h.normal;
					out[0].distance = h.distance;
					out[0].gameObject = h.gameObject ? h.gameObject->GetFileID() : 0;
					hits.fetch_add(1, std::memory_order_relaxed);
					continue;
				}
				all.resize((size_t)(std::min)(maxHits * 4, 256));
				const int n = pm->RaycastAll(c.from, dir, dist, all.data(), (int)all.size(), false);
				int w = 0;
				for (int k = 0; k < n && w < maxHits; ++k)
				{
					const RaycastHit& h = all[k];
					if (h.gameObject == nullptr || !(c.layerMask & (1 << h.gameObject->GetLayerIndex()))) continue;
					if (!triggers && h.collider && h.collider->IsTrigger()) continue;
					out[w].point = h.point;
					out[w].normal = h.normal;
					out[w].distance = h.distance;
					out[w].gameObject = h.gameObject->GetFileID();
					++w;
				}
				hits.fetch_add(w, std::memory_order_relaxed);
			}
		});
		return hits.load();
	}

	// ---------------------------------------------------------------- C# Mesh (런타임 메시 — new Mesh(), SetVertices · SetTriangles, MeshFilter.mesh)
	// 손잡이 (int) → 메시. 같은 메시는 같은 손잡이 (C# 에서 같은 객체). 바뀐 메시는 프레임마다 (ScriptBindings::Update) GPU 로 올린다
	std::unordered_map<int, std::shared_ptr<Mesh>> s_ScriptMeshes;
	std::unordered_map<Mesh*, int> s_ScriptMeshHandles;
	std::unordered_set<int> s_DirtyMeshes;
	int s_NextMeshHandle = 1;

	int RegisterMesh(const std::shared_ptr<Mesh>& m)
	{
		auto it = s_ScriptMeshHandles.find(m.get());
		if (it != s_ScriptMeshHandles.end()) return it->second;
		const int h = s_NextMeshHandle++;
		s_ScriptMeshes[h] = m;
		s_ScriptMeshHandles[m.get()] = h;
		return h;
	}
	Mesh* MeshOf(int h) { auto it = s_ScriptMeshes.find(h); return it == s_ScriptMeshes.end() ? nullptr : it->second.get(); }

	void UploadMesh(Mesh* m)
	{
		if (m->Vertices.empty() || m->Indices.empty()) return;
		const size_t vc = m->Vertices.size();
		for (USHORT& i : m->Indices) if (i >= vc) i = 0;   // 범위 밖 색인 (GPU 가 엉뚱한 곳을 읽지 않게)
		MeshGeometry::Subset s;
		s.Id = 0;
		s.VertexStart = 0;
		s.VertexCount = (uint32)vc;
		s.FaceStart = 0;
		s.FaceCount = (uint32)(m->Indices.size() / 3);
		s.MaterialIndex = 0;
		m->Subsets.assign(1, s);
		if (m->Mat.empty())
		{
			Material mat;
			mat.Ambient = XMFLOAT4(0.8f, 0.8f, 0.8f, 1.0f);
			mat.Diffuse = XMFLOAT4(0.8f, 0.8f, 0.8f, 1.0f);
			mat.Specular = XMFLOAT4(0.4f, 0.4f, 0.4f, 16.0f);
			m->Mat.push_back(mat);
		}
		m->Setup();
		++MeshFilter::s_BindingSerial;   // 컬링이 경계 구를 다시 본다
	}

	int MESH_Create()
	{
		auto m = std::make_shared<Mesh>();
		m->Name = "New Mesh";
		return RegisterMesh(m);
	}
	void MESH_Release(int h)
	{
		auto it = s_ScriptMeshes.find(h);
		if (it == s_ScriptMeshes.end()) return;
		s_ScriptMeshHandles.erase(it->second.get());
		s_DirtyMeshes.erase(h);
		s_ScriptMeshes.erase(it);   // MeshFilter 가 들고 있으면 메시는 남는다 (shared_ptr)
	}
	// MeshFilter 의 메시 (없으면 0)
	int MESH_FromFilter(uint64 go)
	{
		GameObject* g = Find(go);
		MeshFilter* mf = g ? g->GetComponent<MeshFilter>() : nullptr;
		return mf && mf->GetMesh() ? RegisterMesh(mf->GetMesh()) : 0;
	}
	// MeshFilter.mesh = (MeshFilter 가 없으면 0)
	int MESH_SetToFilter(uint64 go, int h)
	{
		GameObject* g = Find(go);
		MeshFilter* mf = g ? g->GetComponent<MeshFilter>() : nullptr;
		if (mf == nullptr) return 0;
		if (h == 0) { mf->SetMesh(nullptr, L"", 0); return 1; }
		auto it = s_ScriptMeshes.find(h);
		if (it == s_ScriptMeshes.end()) return 0;
		if (s_DirtyMeshes.erase(h)) UploadMesh(it->second.get());
		mf->SetMesh(it->second, L"", 0);
		return 1;
	}
	// 0 vertexCount, 1 index count, 2 subMeshCount
	int MESH_GetInt(int h, int what)
	{
		Mesh* m = MeshOf(h);
		if (m == nullptr) return 0;
		switch (what)
		{
		case 0: return (int)m->Vertices.size();
		case 1: return (int)m->Indices.size();
		default: return m->Indices.empty() ? 0 : 1;
		}
	}
	u8* MESH_GetName(int h) { Mesh* m = MeshOf(h); return Ret(m ? m->Name : std::string()); }
	void MESH_SetName(int h, u8* name) { if (Mesh* m = MeshOf(h)) m->Name = name ? (const char*)name : ""; }

	// 정점 속성 쓰기: attr 0 position (3), 1 normal (3), 2 uv0 (2), 3 tangent (4). position 은 정점 수를 바꾼다, 나머지는 같아야. stride = 바이트
	//  돌려준 값: 1 성공, 0 손잡이 · 수가 맞지 않음, -1 정점 65535 개 넘음 (16 비트 색인)
	int MESH_SetAttr(int h, int attr, const float* data, int count, int dim, int stride)
	{
		Mesh* m = MeshOf(h);
		if (m == nullptr || count < 0 || (count > 0 && data == nullptr)) return 0;
		if (attr == 0)
		{
			if (count > 65535) return -1;
			m->Vertices.resize((size_t)count);
		}
		else if ((size_t)count != m->Vertices.size())
			return 0;
		const uint8_t* p = (const uint8_t*)data;
		if (stride <= 0) stride = dim * 4;
		for (int i = 0; i < count; ++i, p += stride)
		{
			const float* v = (const float*)p;
			Vertex::PosNormalTexTan2& o = m->Vertices[(size_t)i];
			switch (attr)
			{
			case 0: o.pos = XMFLOAT3(v[0], dim > 1 ? v[1] : 0.0f, dim > 2 ? v[2] : 0.0f); break;
			case 1: o.normal = XMFLOAT3(v[0], dim > 1 ? v[1] : 0.0f, dim > 2 ? v[2] : 0.0f); break;
			case 2: o.tex = XMFLOAT2(v[0], dim > 1 ? v[1] : 0.0f); break;
			default: o.tangentU = XMFLOAT4(v[0], dim > 1 ? v[1] : 0.0f, dim > 2 ? v[2] : 0.0f, dim > 3 ? v[3] : 1.0f); break;
			}
		}
		s_DirtyMeshes.insert(h);
		return 1;
	}
	int MESH_GetAttr(int h, int attr, float* out, int count, int dim)
	{
		Mesh* m = MeshOf(h);
		if (m == nullptr || out == nullptr) return 0;
		const int n = (std::min)(count, (int)m->Vertices.size());
		for (int i = 0; i < n; ++i)
		{
			const Vertex::PosNormalTexTan2& v = m->Vertices[(size_t)i];
			const float src[4] = {
				attr == 0 ? v.pos.x : attr == 1 ? v.normal.x : attr == 2 ? v.tex.x : v.tangentU.x,
				attr == 0 ? v.pos.y : attr == 1 ? v.normal.y : attr == 2 ? v.tex.y : v.tangentU.y,
				attr == 0 ? v.pos.z : attr == 1 ? v.normal.z : attr == 2 ? 0.0f : v.tangentU.z,
				attr == 3 ? v.tangentU.w : 0.0f };
			for (int k = 0; k < dim; ++k) out[(size_t)i * dim + k] = src[k];
		}
		return n;
	}
	// 색인 (삼각형 셋씩). is16 = ushort 배열, 아니면 int
	int MESH_SetIndices(int h, const void* data, int count, int is16)
	{
		Mesh* m = MeshOf(h);
		if (m == nullptr || count < 0 || (count > 0 && data == nullptr)) return 0;
		m->Indices.resize((size_t)count);
		const int vc = (int)m->Vertices.size();
		int bad = 0;
		for (int i = 0; i < count; ++i)
		{
			const int v = is16 ? (int)((const uint16_t*)data)[i] : ((const int*)data)[i];
			if (v < 0 || v >= vc) ++bad;
			m->Indices[(size_t)i] = (USHORT)(std::clamp)(v, 0, 65535);
		}
		s_DirtyMeshes.insert(h);
		return bad == 0 ? 1 : -2;   // -2: 정점 수 밖 색인이 있다 (Unity 와 같이 오류)
	}
	int MESH_GetIndices(int h, int* out, int count)
	{
		Mesh* m = MeshOf(h);
		if (m == nullptr || out == nullptr) return 0;
		const int n = (std::min)(count, (int)m->Indices.size());
		for (int i = 0; i < n; ++i) out[i] = m->Indices[(size_t)i];
		return n;
	}
	// 0 RecalculateNormals (면적 가중), 1 RecalculateBounds, 2 RecalculateTangents, 3 Clear, 4 지금 올리기 (UploadMeshData)
	void MESH_Op(int h, int op)
	{
		Mesh* m = MeshOf(h);
		if (m == nullptr) return;
		auto& V = m->Vertices;
		auto& I = m->Indices;
		if (op == 0)
		{
			std::vector<Vec3> n(V.size(), Vec3::Zero);
			for (size_t k = 0; k + 2 < I.size(); k += 3)
			{
				const Vec3 a(V[I[k]].pos), b(V[I[k + 1]].pos), c(V[I[k + 2]].pos);
				const Vec3 f = (b - a).Cross(c - a);   // 시계 방향 (Unity 와 같은 왼손) → 바깥
				n[I[k]] += f; n[I[k + 1]] += f; n[I[k + 2]] += f;
			}
			for (size_t i = 0; i < V.size(); ++i)
			{
				Vec3 v = n[i];
				if (v.LengthSquared() > 1e-20f) v.Normalize(); else v = Vec3(0, 1, 0);
				V[i].normal = XMFLOAT3(v.x, v.y, v.z);
			}
		}
		else if (op == 2)
		{
			std::vector<Vec3> tan(V.size(), Vec3::Zero), bit(V.size(), Vec3::Zero);
			for (size_t k = 0; k + 2 < I.size(); k += 3)
			{
				const auto& v0 = V[I[k]]; const auto& v1 = V[I[k + 1]]; const auto& v2 = V[I[k + 2]];
				const Vec3 e1 = Vec3(v1.pos) - Vec3(v0.pos), e2 = Vec3(v2.pos) - Vec3(v0.pos);
				const float du1 = v1.tex.x - v0.tex.x, dv1 = v1.tex.y - v0.tex.y, du2 = v2.tex.x - v0.tex.x, dv2 = v2.tex.y - v0.tex.y;
				const float det = du1 * dv2 - du2 * dv1;
				if (fabsf(det) < 1e-12f) continue;
				const float r = 1.0f / det;
				const Vec3 t = (e1 * dv2 - e2 * dv1) * r, b = (e2 * du1 - e1 * du2) * r;
				for (int j = 0; j < 3; ++j) { tan[I[k + j]] += t; bit[I[k + j]] += b; }
			}
			for (size_t i = 0; i < V.size(); ++i)
			{
				const Vec3 nrm(V[i].normal);
				Vec3 t = tan[i] - nrm * nrm.Dot(tan[i]);
				if (t.LengthSquared() > 1e-20f) t.Normalize(); else t = Vec3(1, 0, 0);
				const float w = nrm.Cross(t).Dot(bit[i]) < 0.0f ? -1.0f : 1.0f;
				V[i].tangentU = XMFLOAT4(t.x, t.y, t.z, w);
			}
		}
		else if (op == 3)
		{
			V.clear();
			I.clear();
			m->Subsets.clear();
		}
		if (op == 4) { s_DirtyMeshes.erase(h); UploadMesh(m); }
		else s_DirtyMeshes.insert(h);
	}
	// 경계 (center, extents) — 정점에서
	void MESH_Bounds(int h, Vec3* center, Vec3* extents)
	{
		Mesh* m = MeshOf(h);
		Vec3 lo(FLT_MAX, FLT_MAX, FLT_MAX), hi(-FLT_MAX, -FLT_MAX, -FLT_MAX);
		if (m) for (const auto& v : m->Vertices) { lo = Vec3::Min(lo, Vec3(v.pos)); hi = Vec3::Max(hi, Vec3(v.pos)); }
		if (!m || m->Vertices.empty()) lo = hi = Vec3::Zero;
		if (center) *center = (lo + hi) * 0.5f;
		if (extents) *extents = (hi - lo) * 0.5f;
	}

	void FlushScriptMeshes()
	{
		if (s_DirtyMeshes.empty()) return;
		for (int h : s_DirtyMeshes)
			if (Mesh* m = MeshOf(h)) UploadMesh(m);
		s_DirtyMeshes.clear();
	}
}

namespace ScriptBindings
{
	int TableSize() { return (int)sizeof(NativeApiTable); }

	void Fill(void* p)
	{
		LARGE_INTEGER f, c;
		::QueryPerformanceFrequency(&f);
		::QueryPerformanceCounter(&c);
		s_StartTime = (double)c.QuadPart / (double)f.QuadPart;

		NativeApiTable& t = *static_cast<NativeApiTable*>(p);
		t = NativeApiTable{};
		t.Size = (int)sizeof(NativeApiTable);
		t.Log = Log;
		t.GO_IsValid = GO_IsValid; t.GO_GetName = GO_GetName; t.GO_SetName = GO_SetName;
		t.GO_GetActive = GO_GetActive; t.GO_SetActive = GO_SetActive; t.GO_GetTag = GO_GetTag; t.GO_SetTag = GO_SetTag;
		t.GO_Find = GO_Find; t.GO_Create = GO_Create; t.GO_CreatePrimitive = GO_CreatePrimitive; t.GO_Destroy = GO_Destroy;
		t.GO_Instantiate = GO_Instantiate; t.GO_HasComponent = GO_HasComponent; t.GO_AddComponent = GO_AddComponent;
		t.GO_RemoveComponent = GO_RemoveComponent;
		t.TR_GetVector = TR_GetVector; t.TR_SetVector = TR_SetVector; t.TR_GetQuat = TR_GetQuat; t.TR_SetQuat = TR_SetQuat;
		t.TR_GetParent = TR_GetParent; t.TR_SetParent = TR_SetParent; t.TR_GetChildCount = TR_GetChildCount; t.TR_GetChild = TR_GetChild;
		t.Time_Get = Time_Get; t.Input_GetKey = Input_GetKey; t.Input_GetMouseButton = Input_GetMouseButton;
		t.Input_GetMouse = Input_GetMouse; t.Screen_Get = Screen_Get;
		t.RB_GetVector = RB_GetVector; t.RB_SetVector = RB_SetVector; t.RB_AddForce = RB_AddForce;
		t.RB_GetFloat = RB_GetFloat; t.RB_SetFloat = RB_SetFloat; t.RB_GetBool = RB_GetBool; t.RB_SetBool = RB_SetBool; t.RB_Move = RB_Move;
		t.AS_Call = AS_Call; t.AS_GetFloat = AS_GetFloat; t.AS_SetFloat = AS_SetFloat; t.AS_GetBool = AS_GetBool; t.AS_SetBool = AS_SetBool;
		t.AS_PlayOneShot = AS_PlayOneShot; t.AS_GetClip = AS_GetClip; t.AS_SetClip = AS_SetClip; t.Audio_ClipLength = Audio_ClipLength;
		t.PH_Raycast = PH_Raycast; t.Camera_Main = Camera_Main;
		t.Script_SetEnabled = Script_SetEnabled;
		t.UI_GetVec = UIScriptBindings::GetVec;
		t.UI_SetVec = UIScriptBindings::SetVec;
		t.UI_GetString = UIScriptBindings::GetString;
		t.UI_SetString = UIScriptBindings::SetString;
		t.Scene_Load = Scene_Load;
		t.Scene_Active = Scene_Active;
		t.Scene_PathAt = Scene_PathAt;
		t.Scene_Count = Scene_Count;
		t.App_Info = App_Info;
		t.App_ProductName = App_ProductName;
		t.App_Quit = App_Quit;
		t.App_TargetFrameRate = [](int set, int value) -> int {
			if (set)
				Application::targetFrameRate = value;
			return Application::targetFrameRate;
		};
		t.App_LoadingPriority = [](int set, int value) -> int {
			if (set)
				Application::backgroundLoadingPriority = value;
			return Application::backgroundLoadingPriority;
		};
		t.COL_Get = COL_Get;
		t.COL_Set = COL_Set;
		t.Prof_Begin = Prof_Begin;
		t.Prof_End = Prof_End;
		t.Prof_SetThreadName = Prof_SetThreadName;
		t.TR_Gather = TR_Gather;
		t.TR_Scatter = TR_Scatter;
		t.PH_RaycastBatch = PH_RaycastBatch;
		t.MESH_Create = MESH_Create;
		t.MESH_Release = MESH_Release;
		t.MESH_FromFilter = MESH_FromFilter;
		t.MESH_SetToFilter = MESH_SetToFilter;
		t.MESH_GetInt = MESH_GetInt;
		t.MESH_GetName = MESH_GetName;
		t.MESH_SetName = MESH_SetName;
		t.MESH_SetAttr = MESH_SetAttr;
		t.MESH_GetAttr = MESH_GetAttr;
		t.MESH_SetIndices = MESH_SetIndices;
		t.MESH_GetIndices = MESH_GetIndices;
		t.MESH_Op = MESH_Op;
		t.MESH_Bounds = MESH_Bounds;
		t.PS_Call = PS_Call;
		t.PS_GetFloat = PS_GetFloat;
		t.PS_SetFloat = PS_SetFloat;
		t.PS_GetCurve = PS_GetCurve;
		t.PS_SetCurve = PS_SetCurve;
		t.PS_GetColor = PS_GetColor;
		t.PS_SetColor = PS_SetColor;
		t.CC_Move = CC_Move;
		t.CC_GetFloat = CC_GetFloat;
		t.CC_SetFloat = CC_SetFloat;
		t.CC_GetVector = CC_GetVector;
		t.CC_SetVector = CC_SetVector;
		t.CC_GetInt = CC_GetInt;
		t.CC_SetInt = CC_SetInt;
		t.CC_GetHit = CC_GetHit;
		t.JT_GetFloat = JT_GetFloat;
		t.JT_SetFloat = JT_SetFloat;
		t.JT_GetVector = JT_GetVector;
		t.JT_SetVector = JT_SetVector;
		t.JT_GetConnected = JT_GetConnected;
		t.JT_SetConnected = JT_SetConnected;
		t.AS_GetOutput = AS_GetOutput;
		t.AS_SetOutput = AS_SetOutput;
		t.MX_Load = MX_Load;
		t.MX_SetFloat = MX_SetFloat;
		t.MX_GetFloat = MX_GetFloat;
		t.MX_ClearFloat = MX_ClearFloat;
		t.MX_Transition = MX_Transition;
		t.MX_Names = MX_Names;
		t.MX_GroupLevel = MX_GroupLevel;
		t.TX_Info = UIScriptBindings::TextInfo;
		t.TX_Link = [](uint64 id, int index, int which) -> u8* { return (u8*)UIScriptBindings::TextLink(id, index, which); };
		t.UI_RaycastScreen = [](float x, float y) -> uint64 { GameObject* g = UISystem::RaycastScreen(x, y); return g ? g->GetFileID() : 0; };
		t.SMR_Count = [](uint64 id) -> int { SkinnedMeshRenderer* r = Get<SkinnedMeshRenderer>(id); return r ? r->BlendShapeCount() : 0; };
		t.SMR_Name = [](uint64 id, int i) -> u8* { SkinnedMeshRenderer* r = Get<SkinnedMeshRenderer>(id); return Ret(r ? r->BlendShapeName(i) : std::string()); };
		t.SMR_Index = [](uint64 id, u8* name) -> int { SkinnedMeshRenderer* r = Get<SkinnedMeshRenderer>(id); return r && name ? r->BlendShapeIndex((const char*)name) : -1; };
		t.SMR_GetWeight = [](uint64 id, int i) -> float { SkinnedMeshRenderer* r = Get<SkinnedMeshRenderer>(id); return r ? r->GetBlendShapeWeight(i) : 0.0f; };
		t.SMR_SetWeight = [](uint64 id, int i, float w) { if (SkinnedMeshRenderer* r = Get<SkinnedMeshRenderer>(id)) r->SetBlendShapeWeight(i, w); };
		t.SR_GetColor = [](uint64 id, float* out) -> int { SpriteRenderer* r = Get<SpriteRenderer>(id); if (!r || !out) return 0; memcpy(out, r->GetColor(), sizeof(float) * 4); return 1; };
		t.SR_SetColor = [](uint64 id, float* in) { if (SpriteRenderer* r = Get<SpriteRenderer>(id); r && in) r->SetColor(in); };
		t.SR_GetInt = [](uint64 id, int field) -> int {
			SpriteRenderer* r = Get<SpriteRenderer>(id);
			if (!r) return 0;
			return field == 0 ? (int)r->GetFlipX() : field == 1 ? (int)r->GetFlipY() : r->GetSortingOrder();
		};
		t.SR_SetInt = [](uint64 id, int field, int v) {
			SpriteRenderer* r = Get<SpriteRenderer>(id);
			if (!r) return;
			if (field == 0) r->SetFlipX(v != 0);
			else if (field == 1) r->SetFlipY(v != 0);
			else r->SetSortingOrder(v);
		};
		t.SR_GetSprite = [](uint64 id) -> u8* { SpriteRenderer* r = Get<SpriteRenderer>(id); return Ret(r ? r->GetSprite() : std::string()); };
		t.SR_SetSprite = [](uint64 id, u8* path) { if (SpriteRenderer* r = Get<SpriteRenderer>(id)) r->SetSprite(path ? (const char*)path : ""); };
		t.GO_GetLayer = [](uint64 id) -> int { GameObject* g = Find(id); return g ? (int)g->GetLayerIndex() : 0; };
		t.GO_SetLayer = [](uint64 id, int layer) { if (GameObject* g = Find(id); g && layer >= 0 && layer < 32) g->SetLayerIndex((uint8)layer); };
		t.LM_NameToLayer = [](u8* name) -> int { return name ? TagsAndLayers::NameToLayer((const char*)name) : -1; };
		t.LM_LayerToName = [](int layer) -> u8* { return (u8*)Ret(TagsAndLayers::LayerName(layer)); };
		t.PH_RaycastMask = [](Vec3* origin, Vec3* dir, float maxDistance, int mask, int triggers, RaycastData* out) -> int {
			if (origin == nullptr || dir == nullptr || out == nullptr) return 0;
			*out = RaycastData{};
			RaycastHit hit;
			if (!PhysicsManager::GetI()->Raycast(*origin, *dir, hit, maxDistance, triggers != 0, (uint32)mask))
				return 0;
			out->point = hit.point;
			out->normal = hit.normal;
			out->distance = hit.distance;
			out->gameObject = hit.gameObject ? hit.gameObject->GetFileID() : 0;
			return 1;
		};
		t.PH_IgnoreLayer = [](int a, int b, int ignore) { PhysicsSettings::IgnoreLayerCollisionRuntime(a, b, ignore != 0); };
		t.PH_GetIgnoreLayer = [](int a, int b) -> int { return PhysicsSettings::LayersCollide(a, b) ? 0 : 1; };
		t.PH_GetGravity = [](Vec3* out) { if (out) *out = PhysicsManager::GetI()->GetGravity(); };
		t.PH_SetGravity = [](Vec3* g) { if (g) PhysicsManager::GetI()->SetGravity(*g); };
		t.SA_Play = [](uint64 id, u8* clip) -> int { SpriteAnimator* a = Get<SpriteAnimator>(id); return a && clip && a->Play((const char*)clip) ? 1 : 0; };
		t.SA_Stop = [](uint64 id) { if (SpriteAnimator* a = Get<SpriteAnimator>(id)) a->Stop(); };
		t.SA_GetInt = [](uint64 id, int what) -> int { SpriteAnimator* a = Get<SpriteAnimator>(id); if (!a) return 0; return what == 0 ? (a->IsPlaying() ? 1 : 0) : a->Frame(); };
		t.SA_Clip = [](uint64 id) -> u8* { SpriteAnimator* a = Get<SpriteAnimator>(id); return (u8*)Ret(a ? a->CurrentClip() : std::string()); };
		t.SA_GetSpeed = [](uint64 id) -> float { SpriteAnimator* a = Get<SpriteAnimator>(id); return a ? a->Speed : 0.0f; };
		t.SA_SetSpeed = [](uint64 id, float v) { if (SpriteAnimator* a = Get<SpriteAnimator>(id)) a->Speed = v; };
		t.SR_GetSortingLayer = [](uint64 id) -> u8* { SpriteRenderer* r = Get<SpriteRenderer>(id); return (u8*)Ret(r ? TagsAndLayers::SortingLayerName(r->GetSortingLayerId()) : std::string()); };
		t.CL_GetMask = [](uint64 id, int which) -> int {
			if (which == 0) { Camera* c = Get<Camera>(id); return c ? (int)c->GetCullingMask() : -1; }
			Light* l = Get<Light>(id); return l ? (int)l->GetCullingMaskBits() : -1;
		};
		t.CL_SetMask = [](uint64 id, int which, int mask) {
			if (which == 0) { if (Camera* c = Get<Camera>(id)) c->SetCullingMask((uint32)mask); }
			else if (Light* l = Get<Light>(id)) l->SetCullingMaskBits((uint32)mask);
		};
		t.R2_GetVec = [](uint64 id, int prop, Vec2* out) {
			Rigidbody2D* rb = Get2DBody(id);
			if (!out) return;
			*out = !rb ? Vec2(0, 0) : prop == 0 ? rb->GetVelocity() : rb->GetPosition();
		};
		t.R2_SetVec = [](uint64 id, int prop, Vec2* v) {
			Rigidbody2D* rb = Get2DBody(id);
			if (!rb || !v) return;
			if (prop == 0) rb->SetVelocity(*v);
			else
			{
				// position: 바로 옮긴다 (Transform → 다음 물리 단계에 몸체도)
				Transform* tr = rb->GetGameObject()->GetTransform();
				const Vec3 p = tr->GetPosition();
				tr->SetPosition(Vec3(v->x, v->y, p.z));
			}
		};
		t.R2_GetFloat = [](uint64 id, int prop) -> float {
			Rigidbody2D* rb = Get2DBody(id);
			if (!rb) return 0.0f;
			switch (prop)
			{
			case 0: return rb->GetAngularVelocity();
			case 1: return rb->GetRotation();
			case 2: return rb->Mass;
			case 3: return rb->GravityScale;
			case 4: return rb->LinearDamping;
			case 5: return rb->AngularDamping;
			case 6: return (float)(int)rb->Type;
			case 7: return rb->FreezeRotation ? 1.0f : 0.0f;
			case 8: return (float)rb->CollisionDetection;
			default: return 0.0f;
			}
		};
		t.R2_SetFloat = [](uint64 id, int prop, float v) {
			Rigidbody2D* rb = Get2DBody(id);
			if (!rb) return;
			switch (prop)
			{
			case 0: rb->SetAngularVelocity(v); return;
			case 1:
			{
				Transform* tr = rb->GetGameObject()->GetTransform();
				tr->SetRotation(Quaternion::CreateFromAxisAngle(Vec3(0, 0, 1), v * 3.14159265f / 180.0f));
				return;
			}
			case 2: rb->Mass = (std::max)(0.0001f, v); break;
			case 3: rb->GravityScale = v; break;
			case 4: rb->LinearDamping = (std::max)(0.0f, v); break;
			case 5: rb->AngularDamping = (std::max)(0.0f, v); break;
			case 6: rb->Type = (Rigidbody2D::BodyType)std::clamp((int)v, 0, 2); break;
			case 7: rb->FreezeRotation = v != 0.0f; break;
			case 8: rb->CollisionDetection = v != 0.0f ? 1 : 0; break;
			default: return;
			}
			rb->ApplyDynamicSettings();
		};
		t.R2_Act = [](uint64 id, int kind, Vec2* a, Vec2* b, float value, int mode) {
			Rigidbody2D* rb = Get2DBody(id);
			if (!rb) return;
			switch (kind)
			{
			case 0: if (a) rb->AddForce(*a, mode == 1); break;
			case 1: if (a && b) rb->AddForceAtPosition(*a, *b, mode == 1); break;
			case 2: rb->AddTorque(value, mode == 1); break;
			case 3: if (a) rb->MovePosition(*a); break;
			case 4: rb->MoveRotation(value); break;
			default: break;
			}
		};
		t.C2_Get = [](uint64 id, u8* type, int prop, float* out) -> int {
			GameObject* g = Find(id);
			Collider2D* c = g ? dynamic_cast<Collider2D*>(FindComponent(g, type && type[0] ? (const char*)type : "Collider2D")) : nullptr;
			if (!c || !out) return 0;
			switch (prop)
			{
			case 0: out[0] = c->IsTrigger ? 1.0f : 0.0f; break;
			case 1: out[0] = c->Offset.x; out[1] = c->Offset.y; break;
			case 2:
				if (auto* bx = dynamic_cast<BoxCollider2D*>(c)) { out[0] = bx->Size.x; out[1] = bx->Size.y; }
				else if (auto* cp = dynamic_cast<CapsuleCollider2D*>(c)) { out[0] = cp->Size.x; out[1] = cp->Size.y; }
				break;
			case 3: if (auto* ci = dynamic_cast<CircleCollider2D*>(c)) out[0] = ci->Radius; break;
			case 4: out[0] = c->Friction; break;
			case 5: out[0] = c->Bounciness; break;
			case 6: out[0] = c->IsEnabled() ? 1.0f : 0.0f; break;
			default: return 0;
			}
			return 1;
		};
		t.C2_Set = [](uint64 id, u8* type, int prop, float* in) {
			GameObject* g = Find(id);
			Collider2D* c = g ? dynamic_cast<Collider2D*>(FindComponent(g, type && type[0] ? (const char*)type : "Collider2D")) : nullptr;
			if (!c || !in) return;
			switch (prop)
			{
			case 0: c->IsTrigger = in[0] != 0.0f; break;
			case 1: c->Offset = Vec2(in[0], in[1]); break;
			case 2:
				if (auto* bx = dynamic_cast<BoxCollider2D*>(c)) bx->Size = Vec2(in[0], in[1]);
				else if (auto* cp = dynamic_cast<CapsuleCollider2D*>(c)) cp->Size = Vec2(in[0], in[1]);
				break;
			case 3: if (auto* ci = dynamic_cast<CircleCollider2D*>(c)) ci->Radius = (std::max)(0.0001f, in[0]); break;
			case 4: c->Friction = (std::max)(0.0f, in[0]); break;
			case 5: c->Bounciness = std::clamp(in[0], 0.0f, 1.0f); break;
			case 6: c->SetEnabled(in[0] != 0.0f); break;
			default: return;
			}
			c->Touch();
		};
		t.P2_Raycast = [](Vec2* origin, Vec2* dir, float distance, int mask, Ray2DData* out) -> int {
			if (!origin || !dir || !out) return 0;
			*out = Ray2DData{};
			Physics2DManager::Hit hit;
			if (!Physics2DManager::Raycast(*origin, *dir, distance, (uint32)mask, hit) || !hit.Collider)
				return 0;
			out->point = hit.Point;
			out->normal = hit.Normal;
			out->distance = hit.Distance;
			out->fraction = hit.Fraction;
			out->gameObject = hit.Collider->GetGameObject()->GetFileID();
			return 1;
		};
		t.P2_Overlap = [](int kind, Vec2* p, Vec2* size, float value, int mask) -> uint64 {
			if (!p) return 0;
			Collider2D* c = nullptr;
			if (kind == 2 && size) c = Physics2DManager::OverlapBox(*p, *size, value, (uint32)mask);
			else c = Physics2DManager::OverlapCircle(*p, kind == 1 ? value : 0.0001f, (uint32)mask);
			return c ? c->GetGameObject()->GetFileID() : 0;
		};
		t.P2_Gravity = [](int set, Vec2* g) {
			if (!g) return;
			if (set) Physics2DManager::SetGravity(*g);
			else *g = Physics2DManager::Gravity();
		};
		t.P2_IgnoreLayer = [](int a, int b, int ignore) { Physics2DSettings::IgnoreLayerCollisionRuntime(a, b, ignore != 0); };
		t.P2_GetIgnoreLayer = [](int a, int b) -> int { return Physics2DSettings::LayersCollide(a, b) ? 0 : 1; };
		t.P2_Contact = [](uint64 self, uint64 other, float* out) -> int {
			GameObject* a = Find(self);
			GameObject* b = Find(other);
			Vec2 p, n, v;
			if (!a || !b || !out || !Physics2DManager::ContactInfo(a, b, p, n, v)) return 0;
			out[0] = p.x; out[1] = p.y; out[2] = n.x; out[3] = n.y; out[4] = v.x; out[5] = v.y;
			return 1;
		};
		t.J2_GetFloat = [](uint64 id, int kind, int instance, int prop) -> float {
			auto* j = FindJoint2D(id, kind, instance); if (!j) return 0;
			switch (prop) {
			case 0: return j->BreakForce; case 1: return j->BreakTorque; case 2: return j->EnableCollision;
			case 3: return j->AutoConfigureConnectedAnchor; case 4: return j->UseMotor;
			case 5: return j->MotorSpeed; case 6: return j->MaxMotorForce; case 7: return j->UseLimits;
			case 8: return j->LowerLimit; case 9: return j->UpperLimit; case 10: return j->Distance;
			case 11: return j->AutoConfigureDistance; case 12: return j->DampingRatio; case 13: return j->Frequency;
			case 14: return j->MaxDistanceOnly; case 15: return j->Angle; case 16: return j->AutoConfigureAngle;
			case 17: return j->JointAngle; case 18: return j->JointSpeed; case 19: return j->JointTranslation;
			case 20: return j->MotorForce; case 21: return j->IsEnabled(); case 22: return j->ReactionTorque;
			case 23: return 1; default: return 0;
			}
		};
		t.J2_SetFloat = [](uint64 id, int kind, int instance, int prop, float value) {
			auto* j = FindJoint2D(id, kind, instance); if (!j || std::isnan(value)) return;
			if (prop > 1 && !std::isfinite(value)) return;
			const float positive = (std::max)(0.0f, value);
			switch (prop) {
			case 0: j->BreakForce = positive; break; case 1: j->BreakTorque = positive; break;
			case 2: j->EnableCollision = value != 0; break; case 3: j->AutoConfigureConnectedAnchor = value != 0; break;
			case 4: j->UseMotor = value != 0; break; case 5: j->MotorSpeed = value; break;
			case 6: j->MaxMotorForce = positive; break; case 7: j->UseLimits = value != 0; break;
			case 8: j->LowerLimit = value; break; case 9: j->UpperLimit = value; break;
			case 10: j->Distance = positive; break; case 11: j->AutoConfigureDistance = value != 0; break;
			case 12: j->DampingRatio = positive; break; case 13: j->Frequency = positive; break;
			case 14: j->MaxDistanceOnly = value != 0; break; case 15: j->Angle = value; break;
			case 16: j->AutoConfigureAngle = value != 0; break; case 21: j->SetEnabled(value != 0); break;
			}
		};
		t.J2_GetVec = [](uint64 id, int kind, int instance, int prop, Vec2* out) {
			if (!out) return; *out = Vec2(0, 0); auto* j = FindJoint2D(id, kind, instance); if (!j) return;
			*out = prop == 0 ? j->Anchor : prop == 1 ? j->ConnectedAnchor : j->ReactionForce;
		};
		t.J2_SetVec = [](uint64 id, int kind, int instance, int prop, Vec2* value) {
			auto* j = FindJoint2D(id, kind, instance); if (!j || !value || !std::isfinite(value->x) || !std::isfinite(value->y)) return;
			if (prop == 0) j->Anchor = *value; if (prop == 1) j->ConnectedAnchor = *value;
		};
		t.J2_GetConnected = [](uint64 id, int kind, int instance) -> uint64 { auto* j = FindJoint2D(id, kind, instance); return j ? j->ConnectedBody : 0; };
		t.J2_SetConnected = [](uint64 id, int kind, int instance, uint64 other) { if (auto* j = FindJoint2D(id, kind, instance)) j->ConnectedBody = other; };
		t.J2_Find = [](uint64 id, int kind, int index) -> int { auto* j = FindJoint2D(id, kind, 0, index); return j ? j->GetInstanceID() : 0; };
		t.Scene_LoadOp = Scene_LoadOp;
		t.Scene_OpState = Scene_OpState;
		t.Scene_OpAllow = [](int op, int allow) { SceneManager::GetI()->SetSceneOpAllowActivation(op, allow != 0); };
		t.Scene_Unload = [](int handle) { return SceneManager::GetI()->RequestSceneUnload(handle); };
		t.Scene_LoadedCount = []() { return (int)SceneManager::GetI()->LoadedScenes().size(); };
		t.Scene_HandleAt = [](int i) {
			const auto scenes = SceneManager::GetI()->LoadedScenes();
			return i >= 0 && i < (int)scenes.size() ? scenes[i].Handle : 0;
		};
		t.Scene_HandleInfo = Scene_HandleInfo;
		t.Scene_ActiveHandle = []() { return SceneManager::GetI()->ActiveSceneHandle(); };
		t.Scene_SetActive = [](int handle) { return SceneManager::GetI()->SetActiveSceneHandle(handle) ? 1 : 0; };
		t.Scene_Roots = Scene_Roots;
		t.GO_SceneHandle = [](uint64 id) { GameObject* g = Find(id); return g ? SceneManager::GetI()->SceneHandleOf(g) : 0; };
		t.Prefs_Has = [](u8* k) { return k && PlayerPrefsStore::Has(k) ? 1 : 0; };
		t.Prefs_SetInt = [](u8* k, int v) { if (k) PlayerPrefsStore::SetInt(k, v); };
		t.Prefs_GetInt = [](u8* k, int d) { return k ? PlayerPrefsStore::GetInt(k, d) : d; };
		t.Prefs_SetFloat = [](u8* k, float v) { if (k) PlayerPrefsStore::SetFloat(k, v); };
		t.Prefs_GetFloat = [](u8* k, float d) { return k ? PlayerPrefsStore::GetFloat(k, d) : d; };
		t.Prefs_SetString = [](u8* k, u8* v) { if (k) PlayerPrefsStore::SetString(k, v ? (const char*)v : ""); };
		t.Prefs_GetString = [](u8* k) -> u8* {
			std::string s;
			return k && PlayerPrefsStore::GetString(k, s) ? Ret(s) : nullptr;
		};
		t.Prefs_Delete = [](u8* k) { if (k) PlayerPrefsStore::Delete(k); else PlayerPrefsStore::DeleteAll(); };
		t.Prefs_Save = []() { PlayerPrefsStore::Save(); };
		t.App_Path = [](int what) -> u8* {
			switch (what)
			{
			case 0: return Ret(PlayerPrefsStore::PersistentDataPath());
			case 1: return Ret(wstring_to_string(PathManager::GetI()->GetMovePathW(L"Assets")));
			case 2: return Ret(BuildSettings::GetPlayer().CompanyName);
			case 3: return Ret(BuildSettings::GetPlayer().Version);
			default:
			{
				std::error_code ec;
				const std::filesystem::path tmp = std::filesystem::temp_directory_path(ec) / BuildSettings::ProductName();
				std::filesystem::create_directories(tmp, ec);
				return Ret(tmp.string());
			}
			}
		};
		t.Cam_GetFloat = [](uint64 id, int what) -> float {
			Camera* c = Get<Camera>(id);
			if (c == nullptr) return 0.0f;
			switch (what)
			{
			case 0: return XMConvertToDegrees(c->GetFovY());
			case 1: return c->GetNearZ();
			case 2: return c->GetFarZ();
			case 3: return c->GetOrthoSize();
			case 4: return c->GetAspect();
			case 5: return c->IsOrthographic() ? 1.0f : 0.0f;
			}
			return 0.0f;
		};
		t.Cam_SetFloat = [](uint64 id, int what, float v) {
			Camera* c = Get<Camera>(id);
			if (c == nullptr) return;
			switch (what)
			{
			case 0: c->SetFovY(XMConvertToRadians(std::clamp(v, 0.00001f, 179.0f))); break;
			case 1: c->SetNearZ((std::max)(v, 0.001f)); break;
			case 2: c->SetFarZ((std::max)(v, c->GetNearZ() + 0.01f)); break;
			case 3: c->SetOrthoSize(v); break;
			}
		};
		t.Light_GetFloat = [](uint64 id, int what) -> float {
			Light* l = Get<Light>(id);
			if (l == nullptr) return 0.0f;
			switch (what)
			{
			case 0: return l->GetIntensity();
			case 1: return l->GetShadowStrength();
			case 2: return l->GetRange();
			case 3: return l->GetSpotAngle();
			case 4: return l->GetAreaSize().x;
			case 5: return l->GetAreaSize().y;
			case 6:   // Unity LightType 번호 (Spot 0, Directional 1, Point 2, Rectangle 3, Disc 4)
				switch (l->GetLightType())
				{
				case LightType::Spot: return 0.0f;
				case LightType::Point: return 2.0f;
				case LightType::Rectangle: return 3.0f;
				case LightType::Disc: return 4.0f;
				default: return 1.0f;
				}
			}
			return 0.0f;
		};
		t.Light_SetFloat = [](uint64 id, int what, float v) {
			Light* l = Get<Light>(id);
			if (l == nullptr) return;
			if (what == 0) l->SetIntensity(v);
			else if (what == 1) l->SetShadowStrength(v);
			else if (what == 4) l->SetAreaSize(XMFLOAT2(v, l->GetAreaSize().y));
			else if (what == 5) l->SetAreaSize(XMFLOAT2(l->GetAreaSize().x, v));
		};
		t.Light_GetColor = [](uint64 id, Vec4* out) {
			Light* l = Get<Light>(id);
			const XMFLOAT4 c = l ? l->GetColor() : XMFLOAT4(0, 0, 0, 1);
			if (out) *out = Vec4(c.x, c.y, c.z, 1.0f);
		};
		t.Light_SetColor = [](uint64 id, Vec4* c) { if (Light* l = Get<Light>(id); l && c) l->SetColor(XMFLOAT4(c->x, c->y, c->z, 1.0f)); };
		t.L2D_GetFloat = [](uint64 id, int what) -> float {
			Light2D* l = Get<Light2D>(id);
			if (l == nullptr) return 0.0f;
			switch (what)
			{
			case 0: return (float)(int)l->LightType;
			case 1: return l->Intensity;
			case 2: return l->InnerRadius;
			case 3: return l->OuterRadius;
			case 4: return l->InnerAngle;
			case 5: return l->OuterAngle;
			case 6: return l->Falloff;
			case 7: return l->Shadows ? 1.0f : 0.0f;
			case 8: return l->ShadowStrength;
			case 9: return l->NormalMapDistance;
			}
			return 0.0f;
		};
		t.L2D_SetFloat = [](uint64 id, int what, float v) {
			Light2D* l = Get<Light2D>(id);
			if (l == nullptr) return;
			switch (what)
			{
			case 0: l->LightType = v >= 0.5f ? Light2D::Type::Point : Light2D::Type::Global; break;
			case 1: l->Intensity = (std::max)(0.0f, v); break;
			case 2: l->InnerRadius = std::clamp(v, 0.0f, l->OuterRadius); break;
			case 3: l->OuterRadius = (std::max)(0.01f, v); l->InnerRadius = (std::min)(l->InnerRadius, l->OuterRadius); break;
			case 4: l->InnerAngle = std::clamp(v, 0.0f, l->OuterAngle); break;
			case 5: l->OuterAngle = std::clamp(v, 0.0f, 360.0f); l->InnerAngle = (std::min)(l->InnerAngle, l->OuterAngle); break;
			case 6: l->Falloff = std::clamp(v, 0.0f, 1.0f); break;
			case 7: l->Shadows = v >= 0.5f; break;
			case 8: l->ShadowStrength = std::clamp(v, 0.0f, 1.0f); break;
			case 9: l->NormalMapDistance = (std::max)(0.01f, v); break;
			}
		};
		t.L2D_GetColor = [](uint64 id, Vec4* out) {
			Light2D* l = Get<Light2D>(id);
			if (out) *out = l ? Vec4(l->Color[0], l->Color[1], l->Color[2], 1.0f) : Vec4(1, 1, 1, 1);
		};
		t.L2D_SetColor = [](uint64 id, Vec4* c) { if (Light2D* l = Get<Light2D>(id); l && c) { l->Color[0] = c->x; l->Color[1] = c->y; l->Color[2] = c->z; } };
		t.SC2D_Get = [](uint64 id, int what) -> int {
			ShadowCaster2D* s = Get<ShadowCaster2D>(id);
			return s ? ((what == 0 ? s->CastsShadows : s->SelfShadows) ? 1 : 0) : 0;
		};
		t.SC2D_Set = [](uint64 id, int what, int v) { if (ShadowCaster2D* s = Get<ShadowCaster2D>(id)) (what == 0 ? s->CastsShadows : s->SelfShadows) = v != 0; };
		t.RD_Get = [](uint64 id, int what) -> float {
			Ragdoll* r = Get<Ragdoll>(id);
			if (r == nullptr) return 0.0f;
			switch (what)
			{
			case 0: return r->Active ? 1.0f : 0.0f;
			case 2: return r->BlendTime;
			case 3: return r->IsFaceUp() ? 1.0f : 0.0f;
			case 4: return r->IsBlending() ? 1.0f : 0.0f;
			case 5: return r->AlignRoot ? 1.0f : 0.0f;
			default: return (float)r->Parts.size();
			}
		};
		t.RD_Set = [](uint64 id, int what, float v) {
			Ragdoll* r = Get<Ragdoll>(id);
			if (r == nullptr) return;
			if (what == 0) r->Active = v != 0.0f;
			else if (what == 2) r->BlendTime = (std::max)(0.0f, v);
			else if (what == 5) r->AlignRoot = v != 0.0f;
		};
		t.PH_IgnoreCollision = [](uint64 a, uint64 b, int ignore) { PhysicsManager::GetI()->IgnoreCollision(Find(a), Find(b), ignore != 0); };
		t.PH_GetIgnoreCollision = [](uint64 a, uint64 b) -> int { return PhysicsManager::GetI()->GetIgnoreCollision(Find(a), Find(b)) ? 1 : 0; };
		t.WC_GetFloat = [](uint64 id, int p) -> float {
			WheelCollider* w = Get<WheelCollider>(id);
			if (w == nullptr) return 0.0f;
			if (p == 40) return w->Rpm();
			if (p == 41) return w->IsGrounded() ? 1.0f : 0.0f;
			if (p == 42) return w->SprungMass();
			float* f = WheelFloat(w, p);
			return f ? *f : 0.0f;
		};
		t.WC_SetFloat = [](uint64 id, int p, float v) { if (WheelCollider* w = Get<WheelCollider>(id)) if (float* f = WheelFloat(w, p)) *f = v; };
		t.WC_GetCenter = [](uint64 id, Vec3* out) { WheelCollider* w = Get<WheelCollider>(id); if (out) *out = w ? w->Center : Vec3::Zero; };
		t.WC_SetCenter = [](uint64 id, Vec3* v) { if (WheelCollider* w = Get<WheelCollider>(id); w && v) w->Center = *v; };
		t.WC_GetPose = [](uint64 id, Vec3* pos, Vec4* rot) {
			WheelCollider* w = Get<WheelCollider>(id);
			Vec3 p = Vec3::Zero;
			Quaternion q = Quaternion::Identity;
			if (w) w->GetWorldPose(p, q);
			if (pos) *pos = p;
			if (rot) *rot = Vec4(q.x, q.y, q.z, q.w);
		};
		t.Comp_GetEnabled = Comp_GetEnabled;
		t.Comp_SetEnabled = Comp_SetEnabled;
		t.CL_GetFloat = [](uint64 id, int p) -> float {
			Cloth* c = Get<Cloth>(id);
			if (c == nullptr) return 0.0f;
			switch (p)
			{
			case 0: return c->StretchingStiffness; case 1: return c->BendingStiffness; case 2: return c->UseGravity ? 1.0f : 0.0f; case 3: return c->Damping;
			case 4: return c->Friction; case 5: return c->Thickness; case 6: return c->SolverFrequency; case 7: return (float)c->Pin;
			case 8: return c->IsSimulating() ? 1.0f : 0.0f; default: return (float)c->SimVertexCount();
			}
		};
		t.CL_SetFloat = [](uint64 id, int p, float v) {
			Cloth* c = Get<Cloth>(id);
			if (c == nullptr) return;
			switch (p)
			{
			case 0: c->StretchingStiffness = std::clamp(v, 0.0f, 1.0f); break; case 1: c->BendingStiffness = std::clamp(v, 0.0f, 1.0f); break;
			case 2: c->UseGravity = v != 0.0f; break; case 3: c->Damping = std::clamp(v, 0.0f, 1.0f); break; case 4: c->Friction = (std::max)(0.0f, v); break;
			case 5: c->Thickness = (std::max)(0.0f, v); break; case 6: c->SolverFrequency = v; break; case 7: c->Pin = std::clamp((int)v, 0, 2); break;
			case 20: c->ClearTransformMotion(); break;
			default: break;
			}
		};
		t.CL_GetVector = [](uint64 id, int p, Vec3* out) { Cloth* c = Get<Cloth>(id); if (out) *out = c ? (p == 0 ? c->ExternalAcceleration : c->RandomAcceleration) : Vec3::Zero; };
		t.CL_SetVector = [](uint64 id, int p, Vec3* v) { if (Cloth* c = Get<Cloth>(id); c && v) (p == 0 ? c->ExternalAcceleration : c->RandomAcceleration) = *v; };
		t.CL_GetVertices = [](uint64 id, Vec3* out, int max) -> int {
			Cloth* c = Get<Cloth>(id);
			if (c == nullptr) return 0;
			std::vector<Vec3> v;
			c->LocalVertices(v);
			for (int i = 0; i < (int)v.size() && i < max && out; ++i)
				out[i] = v[i];
			return (int)v.size();
		};
		t.CL_GetCoefficients = [](uint64 id, float* out, int max) -> int {
			Cloth* c = Get<Cloth>(id);
			if (c == nullptr) return 0;
			const int n = c->ClothVertexCount();
			for (int i = 0; i < n && i < max && out; ++i)
			{
				const bool has = i < (int)c->Coefficients.size() && (int)c->Coefficients.size() == n;
				out[i * 2] = has ? c->Coefficients[i].MaxDistance : FLT_MAX;
				out[i * 2 + 1] = has ? c->Coefficients[i].CollisionSphereDistance : FLT_MAX;
			}
			return n;
		};
		t.CL_SetCoefficients = [](uint64 id, float* in, int n) {
			Cloth* c = Get<Cloth>(id);
			if (c == nullptr) return;
			c->Coefficients.clear();
			for (int i = 0; i < n && in; ++i)
				c->Coefficients.push_back({ (std::max)(0.0f, in[i * 2]), (std::max)(0.0f, in[i * 2 + 1]) });
		};
		t.WC_GetHit = [](uint64 id, WheelHitData* out) -> int {
			WheelCollider* w = Get<WheelCollider>(id);
			WheelCollider::GroundHit h;
			if (w == nullptr || out == nullptr || !w->GetGroundHit(h)) return 0;
			out->point = h.Point; out->normal = h.Normal; out->forwardDir = h.ForwardDir; out->sidewaysDir = h.SidewaysDir;
			out->force = h.Force; out->forwardSlip = h.ForwardSlip; out->sidewaysSlip = h.SidewaysSlip;
			out->gameObject = h.Object ? h.Object->GetFileID() : 0;
			return 1;
		};
		t.GO_MoveToScene = [](uint64 id, int handle) { GameObject* g = Find(id); return g && SceneManager::GetI()->MoveRootToScene(g, handle) ? 1 : 0; };
		t.J2_Remove = [](uint64 id, int kind, int instance) {
			SceneManager::GetI()->AddLastUpdate([id, kind, instance]() {
				if (auto* j = FindJoint2D(id, kind, instance))
					if (auto* scene = CurrentScene()) scene->DestroyComponent(j);
			});
		};
		t.SR_SetSortingLayer = [](uint64 id, u8* name) -> int {
			SpriteRenderer* r = Get<SpriteRenderer>(id);
			const int layer = name ? TagsAndLayers::SortingLayerIdFromName((const char*)name) : -1;
			if (!r || layer < 0) return 0;
			r->SetSortingLayerId(layer);
			return 1;
		};
	}

	GameObject* FindObject(uint64 fileID) { return Find(fileID); }
	const char* ReturnString(const std::string& s) { return Ret(s); }

	void BeginFrame()
	{
		s_Cache.clear();
	}

	void Update()
	{
		PlayerPrefsStore::Flush();   // 바뀐 PlayerPrefs 는 프레임마다 쓴다 (앱이 갑자기 꺼져도)
		FlushScriptMeshes();         // C# 이 바꾼 메시를 GPU 로 (그리기 전에, 한 번만)
		if (!Application::IsPlaying() || s_DelayedDestroys.empty())
			return;
		const float now = ScriptEngine::PlayTime();
		for (size_t i = 0; i < s_DelayedDestroys.size();)
		{
			if (s_DelayedDestroys[i].Time <= now)
			{
				DestroyNow(s_DelayedDestroys[i].Id);
				s_DelayedDestroys.erase(s_DelayedDestroys.begin() + i);
			}
			else
				++i;
		}
	}

	void Reset()
	{
		s_Cache.clear();
		s_Created.clear();
		s_DelayedDestroys.clear();
	}
}
