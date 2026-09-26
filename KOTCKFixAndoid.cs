using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using TeamCherry.Localization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KOTCKFixAndroid
{
    // Android port of KOTCKFIX (King of the Coral Kin memory boss fixes/tweaks).
    // Differences from the PC mod:
    //  - Harmony.CreateAndPatchAll -> new Harmony() + manual raw-MethodInfo patches
    //  - Silksong.FsmUtil isn't used; the two helpers it calls (GetFirstActionOfType /
    //    GetLastActionOfType) are implemented locally below
    //  - setupBossValues() runs each line independently and logs a warning instead of throwing if
    //    a named state/action isn't found on this build (the PC version would silently do nothing
    //    past the first missing one, since the bridge swallows exceptions)
    //  - the Language.Get postfix uses the full parameter list (key, sheetTitle, ref __result),
    //    which is the pattern confirmed to actually install on this bridge
    //  - coroutines are started through a Guard helper so anything they throw gets logged
    // Change the GUID/name below to your own.
    [BepInPlugin("com.yourname.kotckfixandroid", "KOTCKFIX Android v5", "0.5.0")]
    public class KOTCKFixPlugin : BaseUnityPlugin
    {
        private enum CoralSpikeState { UPPERCUT, JAB, AIRJAB }

        internal static ManualLogSource Log;
        private static KOTCKFixPlugin Instance;

        private static bool inCoralMemory;
        private static bool teleportToBoss;
        private static bool disableContactDamage;

        private static PlayMakerFSM bossControlFSM;
        private static CoralSpikeState coralSpikeFSMState;
        private static bool P2;
        private static bool P3;
        private static bool scheduledCoralRain;
        private static int groundHits;
        private static bool crossed;
        private static bool threeSpiked;
        private static bool crossCooldown;

        private static readonly System.Collections.Generic.List<GameObject> longSpear = new System.Collections.Generic.List<GameObject>();
        private static readonly System.Collections.Generic.List<GameObject> uppercutSpear = new System.Collections.Generic.List<GameObject>();
        private static readonly System.Collections.Generic.List<GameObject> airSpear = new System.Collections.Generic.List<GameObject>();

        // Shortest scheduling delay we'll allow. Going below this makes coroutines fire in the
        // same frame the state was entered, which skips past states instead of just pacing fast.
        private const float MinDelay = 0.1f;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            try
            {
                try
                {
                    teleportToBoss = Config.Bind("King Of The Coral Kin", "TeleportOnSuperDash", true,
                        "If true, the silksoar will take hornet directly up to the boss, skipping everything else").Value;
                    disableContactDamage = Config.Bind("King Of The Coral Kin", "DisableContactDamage", true,
                        "If true, the boss contact damage will be disabled").Value;
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Config failed, using defaults (true/true): " + ex.Message);
                    teleportToBoss = true;
                    disableContactDamage = true;
                }

                var harmony = new Harmony();   // parameterless

                Install(harmony, "ActivateGameObject.OnEnter",
                    AccessTools.Method(typeof(ActivateGameObject), "OnEnter"),
                    null, nameof(SpikePostfix));

                Install(harmony, "FsmState.OnEnter",
                    AccessTools.Method(typeof(FsmState), "OnEnter"),
                    null, nameof(OnFsmStateEntered));

                Install(harmony, "Language.Get(string,string)",
                    AccessTools.Method(typeof(Language), "Get", new Type[] { typeof(string), typeof(string) }),
                    null, nameof(TitlePostfix));

                SceneManager.sceneLoaded += SceneLoadSetup;

                Log.LogInfo("KOTCKFIX Android v5 loaded");
            }
            catch (Exception ex)
            {
                Log.LogError("Awake failed: " + ex);
            }
        }

        private static void Install(Harmony harmony, string label, MethodInfo original, string prefixName, string postfixName)
        {
            try
            {
                if (original == null)
                {
                    Log.LogError("Target not found: " + label);
                    return;
                }

                MethodInfo prefix = prefixName != null ? AccessTools.Method(typeof(KOTCKFixPlugin), prefixName) : null;
                MethodInfo postfix = postfixName != null ? AccessTools.Method(typeof(KOTCKFixPlugin), postfixName) : null;

                harmony.Patch(original, prefix: prefix, postfix: postfix);
                Log.LogInfo("Requested patch for " + label);
            }
            catch (Exception ex)
            {
                Log.LogError("Patching " + label + " failed: " + ex);
            }
        }

        private void FixedUpdate()
        {
            try
            {
                if (!inCoralMemory || !teleportToBoss) return;

                HeroController hero = HeroController.instance;
                if (hero == null || !hero.cState.superDashing) return;

                string activeStateName = GameCameras.instance.cameraFadeFSM.ActiveStateName;
                if (activeStateName != "Scene Fade Out" && activeStateName != "Scene Fade In")
                    StartCoroutine(Guard(FadeTeleport(), "FadeTeleport"));

                float y = hero.transform.position.y;
                if (y > 40f && y < 50f)
                    hero.transform.position = new Vector3(55.2f, 500f, 0.004f);
            }
            catch (Exception ex)
            {
                Log.LogError("FixedUpdate failed: " + ex);
            }
        }

        // ---------- scene setup ----------

        private static void SceneLoadSetup(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (scene.name != "Memory_Coral_Tower")
                {
                    inCoralMemory = false;
                    return;
                }

                inCoralMemory = true;
                if (PlayerData.instance != null) PlayerData.instance.encounteredCoralKing = true;
                Log.LogInfo("Coral Kin memory arena loaded");
            }
            catch (Exception ex)
            {
                Log.LogError("SceneLoadSetup failed: " + ex);
            }
        }

        // ---------- spike spawning ----------

        private static void SpikePostfix(ActivateGameObject __instance)
        {
            try
            {
                if (!inCoralMemory || __instance == null) return;

                GameObject go = __instance.activatedGameObject;
                if (go == null || !go.name.StartsWith("Coral_spear_long")) return;

                switch (coralSpikeFSMState)
                {
                    case CoralSpikeState.UPPERCUT:
                        SpawnSpikeUppercut(go);
                        break;
                    case CoralSpikeState.JAB:
                        SpawnSpikeJab(go);
                        break;
                    case CoralSpikeState.AIRJAB:
                        SpawnSpikeAirJab(go);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.LogError("SpikePostfix failed: " + ex);
            }
        }

        private static void SpawnSpikeUppercut(GameObject go)
        {
            Instance.StartCoroutine(Guard(SpawnSpike(go, P2 ? 8 : 10, Vector3.right, 0.05f), "SpawnSpike"));
            Instance.StartCoroutine(Guard(SpawnSpike(go, P2 ? -8 : -10, Vector3.right, 0.05f), "SpawnSpike"));

            if (P2)
            {
                Instance.StartCoroutine(Guard(SpawnSpike(go, 16, Vector3.right, 0.2f), "SpawnSpike"));
                Instance.StartCoroutine(Guard(SpawnSpike(go, -16, Vector3.right, 0.2f), "SpawnSpike"));
                Instance.StartCoroutine(Guard(SpawnSpike(go, 24, Vector3.right, 0.3f), "SpawnSpike"));
                Instance.StartCoroutine(Guard(SpawnSpike(go, -24, Vector3.right, 0.3f), "SpawnSpike"));
            }

            if (P3 && !threeSpiked)
            {
                threeSpiked = true;
                int dist = UnityEngine.Random.value < 0.5f ? 4 : -4;
                Instance.StartCoroutine(Guard(SpawnSpike(go, dist, Vector3.right, 0.1f), "SpawnSpike"));
                Instance.StartCoroutine(Guard(DisableFlag(() => threeSpiked = false, 0.5f), "DisableThreeSpiked"));
            }
        }

        private static void SpawnSpikeJab(GameObject go)
        {
            GameObject clone = GetNewSpike(go);
            if (clone != null)
            {
                clone.transform.position = go.transform.position;
                clone.transform.rotation = go.transform.rotation;
                clone.transform.localScale = go.transform.localScale;
                clone.SetActive(true);
                Instance.StartCoroutine(Guard(DisableClone(clone), "DisableClone"));
            }

            go.SetActive(false);

            if (!crossed)
            {
                int dist = P2 ? (UnityEngine.Random.value < 0.5f ? 4 : 8) : 8;
                Instance.StartCoroutine(Guard(SpawnSpike(go, dist, Vector3.up, 0.1f), "SpawnSpike"));
            }
            else
            {
                crossed = false;
                Instance.StartCoroutine(Guard(SpawnSpike(go, 8, Vector3.up, 0.1f), "SpawnSpike"));
            }
        }

        private static void SpawnSpikeAirJab(GameObject go)
        {
            GameObject clone = GetNewSpike(go);
            if (clone != null)
            {
                clone.transform.position = go.transform.position;
                clone.transform.rotation = go.transform.rotation;
                clone.transform.localScale = go.transform.localScale;
                clone.SetActive(true);
                Instance.StartCoroutine(Guard(DisableClone(clone), "DisableClone"));
            }

            Instance.StartCoroutine(Guard(SpawnSpike(go, P2 ? 11 : 12, Vector3.right, 0.05f), "SpawnSpike"));
            Instance.StartCoroutine(Guard(SpawnSpike(go, P2 ? -11 : -12, Vector3.right, 0.05f), "SpawnSpike"));

            if (P2)
            {
                Instance.StartCoroutine(Guard(SpawnSpike(go, 22, Vector3.right, 0.2f), "SpawnSpike"));
                Instance.StartCoroutine(Guard(SpawnSpike(go, -22, Vector3.right, 0.2f), "SpawnSpike"));
                Instance.StartCoroutine(Guard(SpawnSpike(go, 33, Vector3.right, 0.3f), "SpawnSpike"));
                Instance.StartCoroutine(Guard(SpawnSpike(go, -33, Vector3.right, 0.3f), "SpawnSpike"));
            }

            if (P3)
            {
                int dist = UnityEngine.Random.value < 0.5f ? 5 : -5;
                Instance.StartCoroutine(Guard(SpawnSpike(go, dist, Vector3.right, 0.1f), "SpawnSpike"));
            }
        }

        private static GameObject GetNewSpike(GameObject go)
        {
            var pool = coralSpikeFSMState == CoralSpikeState.UPPERCUT ? uppercutSpear
                     : coralSpikeFSMState == CoralSpikeState.JAB ? longSpear
                     : coralSpikeFSMState == CoralSpikeState.AIRJAB ? airSpear
                     : null;
            if (pool == null) return null;

            foreach (GameObject candidate in pool)
            {
                if (candidate != null && !candidate.activeSelf) return candidate;
            }

            GameObject clone = UnityEngine.Object.Instantiate(go, go.transform.parent);
            clone.name = clone.name + "_POOLED";
            pool.Add(clone);
            return clone;
        }

        private static void ResetSpikePools()
        {
            longSpear.Clear();
            uppercutSpear.Clear();
            airSpear.Clear();
        }

        // ---------- boss state machine hook ----------

        private static void OnFsmStateEntered(FsmState __instance)
        {
            try
            {
                if (!inCoralMemory || __instance == null) return;

                switch (__instance.Name)
                {
                    case "Drop In":
                        bossControlFSM = __instance.Fsm.FsmComponent;
                        if (disableContactDamage) RemoveContactDamage(bossControlFSM);
                        SetupBossValues(bossControlFSM);
                        P2 = false; P3 = false;
                        scheduledCoralRain = false;
                        groundHits = 0;
                        ResetSpikePools();
                        break;

                    case "Hornet Dead":
                        P2 = false; P3 = false;
                        scheduledCoralRain = false;
                        groundHits = 0;
                        crossed = false;
                        ResetSpikePools();
                        break;

                    case "Death Stagger":
                        inCoralMemory = false;
                        P2 = false; P3 = false;
                        scheduledCoralRain = false;
                        groundHits = 0;
                        crossed = false;
                        ResetSpikePools();
                        Instance.StopAllCoroutines();
                        break;

                    case "P2":
                        if (!P2)
                        {
                            P2 = true;
                            Instance.StartCoroutine(Guard(ForceNextState(__instance, "PHASE ROAR", 0f), "ForceNextState"));
                        }
                        break;

                    case "P3":
                        if (!P3)
                        {
                            P3 = true;
                            SetupP3Values(bossControlFSM);   // P3-specific pacing
                            Instance.StartCoroutine(Guard(ForceNextState(__instance, "PHASE ROAR", 0f), "ForceNextState"));
                        }
                        break;

                    case "P3 Roar":
                        string[] choices = { "UC Antic", "Air Jab Aim", "Cross Antic", "Jab Dir" };
                        // v5: P3 0.5, P1/P2 0.75 (v4 had both at 0.75)
                        float p3Gap = P3 ? 0.5f : 0.75f;
                        Instance.StartCoroutine(Guard(ScheduleNextState(choices[UnityEngine.Random.Range(0, choices.Length)], p3Gap), "ScheduleNextState"));
                        break;

                    case "UC Antic":
                        if (coralSpikeFSMState == CoralSpikeState.UPPERCUT)
                            SafeSetState(bossControlFSM, "Air Jab Aim");
                        else
                            coralSpikeFSMState = CoralSpikeState.UPPERCUT;
                        break;

                    case "Jab Antic":
                        if (coralSpikeFSMState == CoralSpikeState.JAB)
                        {
                            string[] opts = { "Cross Antic", "Air Jab Aim", "UC Antic" };
                            SafeSetState(bossControlFSM, opts[UnityEngine.Random.Range(0, opts.Length)]);
                        }
                        else
                        {
                            coralSpikeFSMState = CoralSpikeState.JAB;
                        }
                        break;

                    case "Air Jab Antic":
                        coralSpikeFSMState = CoralSpikeState.AIRJAB;
                        break;

                    case "Cross 2":
                        crossed = true;
                        crossCooldown = true;
                        Instance.StartCoroutine(Guard(DisableFlag(() => crossed = false, 1.5f), "DisableCrossed"));
                        Instance.StartCoroutine(Guard(DisableFlag(() => crossCooldown = false, 3f), "DisableCrossCooldown"));
                        Instance.StartCoroutine(Guard(ForceNextState(__instance, "CROSS CHOP", 0.05f), "ForceNextState"));
                        break;

                    case "Cross Antic":
                        if (crossCooldown)
                        {
                            string next = coralSpikeFSMState == CoralSpikeState.UPPERCUT
                                ? (UnityEngine.Random.value < 0.5f ? "Air Jab Aim" : "Jab Dir")
                                : coralSpikeFSMState == CoralSpikeState.JAB
                                    ? (UnityEngine.Random.value < 0.5f ? "Air Jab Aim" : "UC Antic")
                                    : (UnityEngine.Random.value < 0.5f ? "UC Antic" : "Jab Dir");
                            SafeSetState(bossControlFSM, next);
                            crossCooldown = false;
                        }
                        break;

                    case "Ground Hit":
                        if (groundHits >= 2)
                            groundHits = 0;
                        else
                        {
                            // v5: unchanged from v4 (both branches already at the MinDelay floor)
                            Instance.StartCoroutine(Guard(ScheduleNextState("Ground Hit", MinDelay), "ScheduleNextState"));
                        }
                        groundHits++;
                        break;

                    case "Shoot Pos":
                    case "Shoot Antic":
                        SafeSetState(bossControlFSM, "P3");
                        break;

                    case "Antic":
                        HandleCoralSpikeAntic(__instance);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.LogError("OnFsmStateEntered failed (state '" + __instance.Name + "'): " + ex);
            }
        }

        private static void HandleCoralSpikeAntic(FsmState state)
        {
            PlayMakerFSM comp = state.Fsm.FsmComponent;
            if (comp == null || !comp.gameObject.name.StartsWith("Coral Spike")) return;
            if (!P3) return;

            // v5: v4 was 0.75, -0.25 -> 0.5
            Instance.StartCoroutine(Guard(ScheduleNextState("Air Jab Aim", 0.5f), "ScheduleNextState"));

            if (comp.transform.position.y > 557f)
                UnityEngine.Object.Destroy(comp.gameObject);

            if (!scheduledCoralRain)
            {
                scheduledCoralRain = true;
                // v5: still at the floor (v4 was 0.1)
                Instance.StartCoroutine(Guard(ScheduleNextState("Ground Hit", MinDelay), "ScheduleNextState"));
                Instance.StartCoroutine(Guard(DisableFlag(() => scheduledCoralRain = false, 1f), "DisableScheduledCoralRain"));
            }
        }

        private static void SafeSetState(PlayMakerFSM fsm, string stateName)
        {
            if (fsm == null) return;
            if (fsm.Fsm.GetState(stateName) == null)
            {
                Log.LogWarning("SetState: no state named '" + stateName + "' on '" + fsm.gameObject.name + "'");
                return;
            }
            fsm.SetState(stateName);
        }

        private static void RemoveContactDamage(PlayMakerFSM fsm)
        {
            if (fsm == null) return;
            DamageHero[] hits = fsm.gameObject.GetComponentsInChildren<DamageHero>(true);
            foreach (DamageHero hit in hits) UnityEngine.Object.Destroy(hit);
        }

        // Each line is independent: a missing state/action on this build only skips that one line.
        // The Stun Control lookup is guarded too, otherwise a missing "Stun Control" child would
        // throw out of SetupBossValues and take the rest of the "Drop In" handler with it.
        //
        // v5: P1/P2 unchanged from v4 (0.25 slower than v3).
        private static void SetupBossValues(PlayMakerFSM fsm)
        {
            if (fsm == null) return;

            try
            {
                PlayMakerFSM stunFsm = FSMUtility.LocateMyFSM(fsm.gameObject, "Stun Control");
                if (stunFsm != null) UnityEngine.Object.Destroy(stunFsm);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Stun Control lookup failed: " + ex.Message);
            }

            SetFloatValueField(fsm, "P1", 0f, 0f);
            SetFloatValueField(fsm, "P2", 0f, 0f);
            SetFloatValueField(fsm, "P3", 0f, 0f);
            SetFloatOperatorFloat1(fsm, "Roar Recover", 0.75f);
            SetWaitTime(fsm, "Jab 2", true, 0.55f);
            SetWaitTime(fsm, "Uppercut 2", true, 0.55f);
            SetWaitTime(fsm, "Cross 2", true, 0.55f);
            SetWaitTime(fsm, "Air Jab 2", true, 0.55f);
            SetWaitTime(fsm, "Cross Followup 2", true, 0.55f);
            SetWaitTime(fsm, "Ground Hit", true, 0.55f);
            SetWaitTime(fsm, "Ground Hit", false, 0.55f);
            SetWaitTime(fsm, "Followup Pause", true, 0.75f);
        }

        // v5: P3 is 0.25s faster than v4 (v4 was +0.5 from v3, now +0.25 from v3).
        private static void SetupP3Values(PlayMakerFSM fsm)
        {
            if (fsm == null) return;

            SetFloatOperatorFloat1(fsm, "Roar Recover", 1.0f);   // v4: 1.25, -0.25
            SetWaitTime(fsm, "Jab 2", true, 0.5f);               // v4: 0.75, -0.25
            SetWaitTime(fsm, "Uppercut 2", true, 0.5f);
            SetWaitTime(fsm, "Cross 2", true, 0.5f);
            SetWaitTime(fsm, "Air Jab 2", true, 0.5f);
            SetWaitTime(fsm, "Cross Followup 2", true, 0.5f);
            SetWaitTime(fsm, "Ground Hit", true, 0.5f);
            SetWaitTime(fsm, "Ground Hit", false, 0.5f);
            SetWaitTime(fsm, "Followup Pause", true, 1.5f);      // v4: 1.75, -0.25
        }

        // ---------- local replacements for Silksong.FsmUtil's two helpers ----------

        private static T FirstAction<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction
        {
            FsmState state = fsm.Fsm.GetState(stateName);
            if (state == null) { Log.LogWarning("No state named '" + stateName + "'"); return null; }

            foreach (FsmStateAction action in state.Actions)
                if (action is T typed) return typed;

            Log.LogWarning("State '" + stateName + "' has no action of type " + typeof(T).Name);
            return null;
        }

        private static T LastAction<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction
        {
            FsmState state = fsm.Fsm.GetState(stateName);
            if (state == null) { Log.LogWarning("No state named '" + stateName + "'"); return null; }

            T found = null;
            foreach (FsmStateAction action in state.Actions)
                if (action is T typed) found = typed;

            if (found == null)
                Log.LogWarning("State '" + stateName + "' has no action of type " + typeof(T).Name);
            return found;
        }

        private static void SetFloatValueField(PlayMakerFSM fsm, string stateName, float variableValue, float value)
        {
            try
            {
                SetFloatValue action = FirstAction<SetFloatValue>(fsm, stateName);
                if (action == null) return;
                action.floatVariable = variableValue;
                action.floatValue = value;
            }
            catch (Exception ex)
            {
                Log.LogWarning("SetFloatValueField '" + stateName + "' failed: " + ex.Message);
            }
        }

        private static void SetFloatOperatorFloat1(PlayMakerFSM fsm, string stateName, float value)
        {
            try
            {
                FloatOperator action = FirstAction<FloatOperator>(fsm, stateName);
                if (action == null) return;
                action.float1 = value;
            }
            catch (Exception ex)
            {
                Log.LogWarning("SetFloatOperatorFloat1 '" + stateName + "' failed: " + ex.Message);
            }
        }

        private static void SetWaitTime(PlayMakerFSM fsm, string stateName, bool first, float value)
        {
            try
            {
                Wait action = first ? FirstAction<Wait>(fsm, stateName) : LastAction<Wait>(fsm, stateName);
                if (action == null) return;
                action.time = value;
            }
            catch (Exception ex)
            {
                Log.LogWarning("SetWaitTime '" + stateName + "' failed: " + ex.Message);
            }
        }

        // ---------- boss title ----------

        private static void TitlePostfix(string key, string sheetTitle, ref string __result)
        {
            if (key == "CORAL_KING_SUPER") __result = "King Of The";
            if (key == "CORAL_KING_MAIN") __result = "Coral Kin";
        }

        // ---------- coroutines ----------

        private static IEnumerator Guard(IEnumerator inner, string label)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) break;
                    current = inner.Current;
                }
                catch (Exception ex)
                {
                    Log.LogError(label + " failed: " + ex);
                    break;
                }
                yield return current;
            }
        }

        private static IEnumerator SpawnSpike(GameObject go, int distance, Vector3 direction, float delay)
        {
            yield return new WaitForSeconds(delay);

            GameObject clone = GetNewSpike(go);
            if (clone == null) yield break;

            clone.transform.position = go.transform.position + direction * distance;
            clone.transform.rotation = go.transform.rotation;
            clone.transform.localScale = go.transform.localScale;

            if (coralSpikeFSMState == CoralSpikeState.JAB)
            {
                Vector3 s = clone.transform.localScale;
                clone.transform.localScale = new Vector3(-s.x, s.y, s.z);
                float x = clone.transform.position.x > 50f ? 16.57f : 94.27f;
                clone.transform.position = new Vector3(x, clone.transform.position.y, clone.transform.position.z);
            }

            clone.SetActive(true);
            yield return Guard(DisableClone(clone), "DisableClone");
        }

        private static IEnumerator DisableClone(GameObject clone)
        {
            yield return new WaitForSeconds(3f);
            if (clone != null) clone.SetActive(false);
        }

        private static IEnumerator FadeTeleport()
        {
            GameCameras.instance.cameraFadeFSM.SetState("Scene Fade Out");
            yield return new WaitForSeconds(1.5f);
            GameCameras.instance.cameraFadeFSM.SetState("Scene Fade In");
        }

        private static IEnumerator ForceNextState(FsmState state, string eventName, float delay)
        {
            yield return new WaitForSeconds(delay);

            FsmTransition transition = null;
            foreach (FsmTransition t in state.Transitions)
            {
                if (t.EventName == eventName) { transition = t; break; }
            }
            if (transition != null) state.Fsm.SetState(transition.ToState);
        }

        private static IEnumerator ScheduleNextState(string stateName, float duration)
        {
            yield return new WaitForSeconds(duration);
            SafeSetState(bossControlFSM, stateName);
        }

        private static IEnumerator DisableFlag(Action setFalse, float delay)
        {
            yield return new WaitForSeconds(delay);
            setFalse();
        }
    }
}
