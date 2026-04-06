using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using UnityStandardAssets.Characters.FirstPerson;

public class ThorMLAgent : Agent
{
    [Header("References")]
    [SerializeField] private Camera agentCamera;
    [SerializeField] private BaseAgentComponent baseAgentComponent;

    [Header("Movement")]
    [SerializeField] private float moveStep = 0.03f;
    [SerializeField] private float rotateStep = 8f;
    [SerializeField] private LayerMask interactMask;

    [Header("Look")]
    [SerializeField] private float lookStep = 10f;
    [SerializeField] private float minPitch = -60f;
    [SerializeField] private float maxPitch = 60f;

    [Header("Spawn")]
    [SerializeField] private Transform[] spawnPoints;

    [Header("Bounds")]
    [SerializeField] private float minX = -5f;
    [SerializeField] private float maxX = 5f;
    [SerializeField] private float minZ = -5f;
    [SerializeField] private float maxZ = 5f;

    [Header("Collision Check")]
    [SerializeField] private float wallCheckDistance = 0.2f;
    [SerializeField] private LayerMask wallMask = ~0;

    [Header("Interaction")]
    [SerializeField] private float interactDistance = 1.5f;
    [SerializeField] private float pickupTryDistance = 1.2f;

    [Header("Rewards")]
    [SerializeField] private float stepPenalty = -0.001f;
    [SerializeField] private float interactPenalty = -0.005f;
    [SerializeField] private float wrongTargetPenalty = -0.05f;
    [SerializeField] private float pickupFailPenalty = -0.01f;
    [SerializeField] private float pickupSuccessReward = 1.0f;
    [SerializeField] private float successEndReward = 0.2f;
    [SerializeField] private float approachRewardScale = 0.02f;
    [SerializeField] private float faceRewardScale = 0.003f;
    [SerializeField] private float noTargetPenalty = -0.0005f;

    [Header("Episode")]
    [SerializeField] private int maxStepPerEpisode = 300;

    private CharacterController characterController;
    private SimObjPhysics cachedFrontTarget;

    private object thorAgent;
    private Type thorAgentType;
    private bool hasLoggedThorAgentNull = false;

    private int currentStep = 0;
    private int episodeCount = 0;
    private int successCount = 0;

    private float prevTargetDistance = -1f;
    private float currentPitch = 0f;

    private SimObjPhysics episodeTarget;

    public override void Initialize()
    {
        var debugControllers = FindObjectsOfType<DebugDiscreteAgentController>(true);

        foreach (var c in debugControllers)
        {
            Debug.Log("[ThorMLAgent] DebugController 강제 제거: " + c.gameObject.name);

            Destroy(c); // 🔥 핵심 (disable 말고 destroy)
        }

        var debugInputs = FindObjectsOfType<DebugInputField>(true);
        foreach (var d in debugInputs)
        {
            Destroy(d);
        }

        Debug.Log("===== THOR MLAGENT LATEST SCRIPT RUNNING =====");

        characterController = GetComponent<CharacterController>();

        if (agentCamera == null)
            agentCamera = GetComponentInChildren<Camera>();

        if (baseAgentComponent == null)
            baseAgentComponent = GetComponent<BaseAgentComponent>();

        EnsureThorAgent();

        if (agentCamera != null)
        {
            currentPitch = agentCamera.transform.localEulerAngles.x;
            if (currentPitch > 180f) currentPitch -= 360f;
        }

        Debug.Log(agentCamera != null
            ? "[ThorMLAgent] camera 연결 성공"
            : "[ThorMLAgent] camera 연결 실패");

        Debug.Log(characterController != null
            ? "[ThorMLAgent] CharacterController 연결 성공"
            : "[ThorMLAgent] CharacterController 연결 실패");

        Debug.Log(baseAgentComponent != null
            ? "[ThorMLAgent] baseAgentComponent 연결 성공"
            : "[ThorMLAgent] baseAgentComponent 연결 실패");
    }

    private void EnsureThorAgent()
    {
        if (thorAgent != null) return;
        if (baseAgentComponent == null) return;

        thorAgent = baseAgentComponent.agent;

        if (thorAgent != null)
        {
            thorAgentType = thorAgent.GetType();
            Debug.Log("[ThorMLAgent] thorAgent Lazy 연결 성공");
        }
    }

    public override void OnEpisodeBegin()
    {
        episodeCount++;
        currentStep = 0;
        prevTargetDistance = -1f;

        ResetAgentPose();
        SelectEpisodeTarget();

        if (agentCamera != null)
        {
            currentPitch = 0f;
            Vector3 camEuler = agentCamera.transform.localEulerAngles;
            camEuler.x = currentPitch;
            agentCamera.transform.localEulerAngles = camEuler;
        }

        Debug.Log($"[ThorMLAgent] Episode Start #{episodeCount}");
    }

    private void ResetAgentPose()
    {
        if (characterController != null)
            characterController.enabled = false;

        if (spawnPoints != null && spawnPoints.Length > 0)
        {
            Transform p = spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)];
            transform.position = p.position;
            transform.rotation = p.rotation;
        }

        Vector3 pos = transform.position;
        pos.x = Mathf.Clamp(pos.x, minX, maxX);
        pos.z = Mathf.Clamp(pos.z, minZ, maxZ);
        transform.position = pos;

        if (characterController != null)
            characterController.enabled = true;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 총 9개
        // 1~2: 에이전트 forward x,z
        sensor.AddObservation(transform.forward.x);
        sensor.AddObservation(transform.forward.z);

        // 3~4: 카메라 forward x,z
        if (agentCamera != null)
        {
            sensor.AddObservation(agentCamera.transform.forward.x);
            sensor.AddObservation(agentCamera.transform.forward.z);
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        // 5~6: 타겟 방향 x,z
        if (agentCamera != null && episodeTarget != null)
        {
            Vector3 toTarget = (episodeTarget.transform.position - agentCamera.transform.position).normalized;
            sensor.AddObservation(toTarget.x);
            sensor.AddObservation(toTarget.z);
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        // 7: 타겟 거리
        if (agentCamera != null && episodeTarget != null)
        {
            float dist = Vector3.Distance(agentCamera.transform.position, episodeTarget.transform.position);
            sensor.AddObservation(Mathf.Clamp01(dist / 5f));
        }
        else
        {
            sensor.AddObservation(1f);
        }

        // 8: 정면에 뭔가 있는가
        cachedFrontTarget = GetInteractableTarget();
        sensor.AddObservation(cachedFrontTarget != null ? 1f : 0f);

        // 9: 정면 물체가 타겟인가
        sensor.AddObservation(
            (cachedFrontTarget != null && episodeTarget != null && cachedFrontTarget.ObjectID == episodeTarget.ObjectID) ? 1f : 0f
        );
    }

    private void SelectEpisodeTarget()
    {
        SimObjPhysics[] allObjects = GameObject.FindObjectsOfType<SimObjPhysics>();
        List<SimObjPhysics> candidates = new List<SimObjPhysics>();

        foreach (var obj in allObjects)
        {
            if (obj == null) continue;
            if (obj.PrimaryProperty != SimObjPrimaryProperty.CanPickup) continue;

            string id = obj.ObjectID.ToLower();

            // 어려운 물체 제외
            if (id.Contains("spoon")) continue;
            if (id.Contains("fork")) continue;
            if (id.Contains("knife")) continue;
            if (id.Contains("pen")) continue;
            if (id.Contains("creditcard")) continue;
            if (id.Contains("egg")) continue;
            if (id.Contains("saltshaker")) continue;
            if (id.Contains("statue")) continue;
            if (id.Contains("vase")) continue;

            candidates.Add(obj);
        }

        episodeTarget = candidates.Count > 0
            ? candidates[UnityEngine.Random.Range(0, candidates.Count)]
            : null;

        Debug.Log("[ThorMLAgent] Episode Target = " + (episodeTarget != null ? episodeTarget.ObjectID : "null"));
    }

    private bool IsBlocked(Vector3 dir)
    {
        if (dir == Vector3.zero) return false;

        Vector3 origin = transform.position + Vector3.up * 0.3f;
        return Physics.Raycast(origin, dir.normalized, wallCheckDistance, wallMask, QueryTriggerInteraction.Ignore);
    }

    private SimObjPhysics GetInteractableTarget()
    {
        if (agentCamera == null) return null;

        RaycastHit hit;

        Vector3 origin = agentCamera.transform.position;

        Vector3[] dirs = new Vector3[]
        {
            agentCamera.transform.forward,
            agentCamera.transform.forward + agentCamera.transform.up * 0.1f,
            agentCamera.transform.forward - agentCamera.transform.up * 0.1f
        };

        foreach (var dir in dirs)
        {
            if (Physics.Raycast(origin, dir.normalized, out hit, interactDistance, interactMask))
            {
                SimObjPhysics simObj = hit.collider.GetComponentInParent<SimObjPhysics>();

                if (simObj != null && simObj.PrimaryProperty == SimObjPrimaryProperty.CanPickup)
                {
                    Debug.Log("[ThorMLAgent] 🎯 Hit success: " + simObj.ObjectID);
                    return simObj;
                }
            }
        }

        return null;
    }

    private MethodInfo FindWhatAmIHoldingMethod()
    {
        if (thorAgentType == null) return null;

        MethodInfo[] methods = thorAgentType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        foreach (var m in methods)
        {
            if (m.Name != "WhatAmIHolding") continue;
            if (m.GetParameters().Length == 0)
                return m;
        }

        return null;
    }

    private GameObject GetHoldingObject()
    {
        EnsureThorAgent();

        if (thorAgent == null || thorAgentType == null) return null;

        try
        {
            MethodInfo method = FindWhatAmIHoldingMethod();
            if (method == null)
            {
                Debug.LogError("[ThorMLAgent] WhatAmIHolding 메서드를 찾지 못함");
                return null;
            }

            return method.Invoke(thorAgent, null) as GameObject;
        }
        catch (Exception e)
        {
            Debug.LogError("[ThorMLAgent] WhatAmIHolding 실패: " + e);
            return null;
        }
    }

    private bool IsHoldingEpisodeTarget()
    {
        if (episodeTarget == null) return false;

        GameObject holdingObj = GetHoldingObject();
        if (holdingObj == null) return false;

        SimObjPhysics holding = holdingObj.GetComponent<SimObjPhysics>();
        if (holding == null) return false;

        return holding.ObjectID == episodeTarget.ObjectID;
    }

    private MethodInfo FindPickupMethod()
    {
        if (thorAgentType == null) return null;

        MethodInfo[] methods = thorAgentType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        foreach (var m in methods)
        {
            if (m.Name != "PickupObject") continue;
            var ps = m.GetParameters();

            if (ps.Length == 3 &&
                ps[0].ParameterType == typeof(string) &&
                ps[1].ParameterType == typeof(bool) &&
                ps[2].ParameterType == typeof(bool))
                return m;
        }

        foreach (var m in methods)
        {
            if (m.Name != "PickupObject") continue;
            var ps = m.GetParameters();

            if (ps.Length == 2 &&
                ps[0].ParameterType == typeof(string) &&
                ps[1].ParameterType == typeof(bool))
                return m;
        }

        foreach (var m in methods)
        {
            if (m.Name != "PickupObject") continue;
            var ps = m.GetParameters();

            if (ps.Length == 1 &&
                ps[0].ParameterType == typeof(string))
                return m;
        }

        return null;
    }

    private bool InvokePickupObject(string objectId)
    {
        EnsureThorAgent();

        if (thorAgent == null || thorAgentType == null)
        {
            Debug.LogError("[ThorMLAgent] thorAgent null → pickup 불가");
            return false;
        }

        try
        {
            MethodInfo pickupMethod = FindPickupMethod();

            if (pickupMethod == null)
            {
                Debug.LogError("[ThorMLAgent] PickupObject 오버로드를 찾지 못함");
                return false;
            }

            var ps = pickupMethod.GetParameters();
            Debug.Log("[ThorMLAgent] PickupObject 시그니처 선택: " + ps.Length);

            if (ps.Length == 3)
                pickupMethod.Invoke(thorAgent, new object[] { objectId, false, true });
            else if (ps.Length == 2)
                pickupMethod.Invoke(thorAgent, new object[] { objectId, false });
            else if (ps.Length == 1)
                pickupMethod.Invoke(thorAgent, new object[] { objectId });
            else
                return false;

            Debug.Log("[ThorMLAgent] PickupObject 호출 성공");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[ThorMLAgent] Pickup invoke 실패: " + e);
            return false;
        }
    }

    private bool TryPickupEpisodeTarget()
    {
        EnsureThorAgent();

        if (thorAgent == null || episodeTarget == null) return false;

        if (GetHoldingObject() != null)
            return false;

        SimObjPhysics frontTarget = cachedFrontTarget;
        if (frontTarget == null) return false;
        if (frontTarget.ObjectID != episodeTarget.ObjectID) return false;

        float dist = Vector3.Distance(agentCamera.transform.position, episodeTarget.transform.position);

        Debug.Log(
            "[ThorMLAgent] TryPickup | front=" +
            (frontTarget != null ? frontTarget.ObjectID : "null") +
            " | target=" +
            (episodeTarget != null ? episodeTarget.ObjectID : "null") +
            " | dist=" + dist
        );

        if (dist > pickupTryDistance) return false;

        return InvokePickupObject(frontTarget.ObjectID);
    }

    private void AddDenseReward()
    {
        if (agentCamera == null || episodeTarget == null)
        {
            AddReward(noTargetPenalty);
            prevTargetDistance = -1f;
            return;
        }

        float currentDist = Vector3.Distance(agentCamera.transform.position, episodeTarget.transform.position);

        if (prevTargetDistance > 0f)
        {
            float delta = prevTargetDistance - currentDist;
            AddReward(delta * approachRewardScale);
        }

        prevTargetDistance = currentDist;

        Vector3 toTarget = (episodeTarget.transform.position - agentCamera.transform.position).normalized;
        float alignment = Vector3.Dot(agentCamera.transform.forward, toTarget);
        AddReward(alignment * faceRewardScale);
    }

    public override void OnActionReceived(float[] vectorAction)
    {
        currentStep++;
        SimObjPhysics frontTarget = cachedFrontTarget;

        int move = Mathf.FloorToInt(vectorAction[0]);
        int turn = Mathf.FloorToInt(vectorAction[1]);
        int look = Mathf.FloorToInt(vectorAction[2]);
        int interact = Mathf.FloorToInt(vectorAction[3]);

        Vector3 moveDir = Vector3.zero;

        if (move == 1)
            moveDir = transform.forward * moveStep;
        else if (move == 2)
            moveDir = -transform.forward * moveStep;

        if (!IsBlocked(moveDir))
        {
            if (characterController != null)
                characterController.Move(moveDir);
            else
                transform.position += moveDir;
        }

        if (turn == 1)
            transform.Rotate(Vector3.up, -rotateStep);
        else if (turn == 2)
            transform.Rotate(Vector3.up, rotateStep);

        if (look == 1)
            currentPitch -= lookStep;
        else if (look == 2)
            currentPitch += lookStep;

        currentPitch = Mathf.Clamp(currentPitch, minPitch, maxPitch);

        if (agentCamera != null)
        {
            Vector3 camEuler = agentCamera.transform.localEulerAngles;
            camEuler.x = currentPitch;
            agentCamera.transform.localEulerAngles = camEuler;
        }

        Vector3 pos = transform.position;
        pos.x = Mathf.Clamp(pos.x, minX, maxX);
        pos.z = Mathf.Clamp(pos.z, minZ, maxZ);
        transform.position = pos;

        AddDenseReward();

        if (interact == 1)
        {
            Debug.Log("[ThorMLAgent] interact == 1 진입");

            SimObjPhysics frontTarget = cachedFrontTarget;

            if (frontTarget == null)
            {
                Debug.Log("[ThorMLAgent] ❌ 정면에 pickup 가능한 물체 없음");
                AddReward(pickupFailPenalty);
            }
            else
            {
                Debug.Log("[ThorMLAgent] 🎯 frontTarget = " + frontTarget.ObjectID);
                Debug.Log("[ThorMLAgent] 🎯 episodeTarget = " + (episodeTarget != null ? episodeTarget.ObjectID : "null"));

                if (episodeTarget == null)
                {
                    Debug.Log("[ThorMLAgent] ❌ episodeTarget 없음");
                    AddReward(pickupFailPenalty);
                }
                else if (frontTarget.ObjectID != episodeTarget.ObjectID)
                {
                    Debug.Log("[ThorMLAgent] ❌ 다른 물체를 보고 있음");
                    AddReward(wrongTargetPenalty);
                }
                else
                {
                    bool ok = TryPickupEpisodeTarget();
                    Debug.Log("[ThorMLAgent] Pickup invoke 결과: " + ok);

                    if (!ok)
                        AddReward(pickupFailPenalty);
                }
            }
        }

        if (IsHoldingEpisodeTarget())
        {
            successCount++;
            AddReward(pickupSuccessReward);
            AddReward(successEndReward);

            Debug.Log($"[ThorMLAgent] Pickup 성공 | Target={episodeTarget.ObjectID}");
            Debug.Log($"[ThorMLAgent] SuccessRate = {(float)successCount / episodeCount:F2} ({successCount}/{episodeCount})");
            Debug.Log($"[ThorMLAgent] Episode Reward = {GetCumulativeReward():F3}");

            EndEpisode();
            return;
        }

        AddReward(stepPenalty);

        if (currentStep >= maxStepPerEpisode)
        {
            Debug.Log($"[ThorMLAgent] Episode 실패 종료 | Reward = {GetCumulativeReward():F3} | SuccessRate = {(float)successCount / episodeCount:F2}");
            EndEpisode();
        }
    }

    public override void Heuristic(float[] actionsOut)
    {
        actionsOut[0] = 0f; // move
        actionsOut[1] = 0f; // turn
        actionsOut[2] = 0f; // look
        actionsOut[3] = 0f; // interact

        if (Input.GetKey(KeyCode.W)) actionsOut[0] = 1f;
        if (Input.GetKey(KeyCode.S)) actionsOut[0] = 2f;

        if (Input.GetKey(KeyCode.A)) actionsOut[1] = 1f;
        if (Input.GetKey(KeyCode.D)) actionsOut[1] = 2f;

        if (Input.GetKey(KeyCode.R)) actionsOut[2] = 1f; // look up
        if (Input.GetKey(KeyCode.F)) actionsOut[2] = 2f; // look down

        if (Input.GetKey(KeyCode.E))
        {
            actionsOut[3] = 1f;
            Debug.Log("[ThorMLAgent] E 입력 감지");
        }
    }
}