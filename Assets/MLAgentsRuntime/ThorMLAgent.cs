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
    [SerializeField] private float pickupSuccessReward = 1.0f;
    [SerializeField] private float pickupFailPenalty = -0.01f;
    [SerializeField] private float wrongTargetPenalty = -0.01f;
    [SerializeField] private float approachRewardScale = 0.02f;
    [SerializeField] private float faceRewardScale = 0.003f;
    [SerializeField] private float noTargetPenalty = -0.0005f;
    [SerializeField] private float successEndReward = 0.2f;

    [Header("Episode")]
    [SerializeField] private int maxStepPerEpisode = 300;

    private Vector3 startPosition;
    private Quaternion startRotation;
    private CharacterController characterController;

    // 직접 타입 참조 대신 reflection 사용
    private object thorAgent;
    private Type thorAgentType;

    private float prevTargetDistance = -1f;
    private int currentStep = 0;

    private int episodeCount = 0;
    private int successCount = 0;

    private SimObjPhysics episodeTarget;

    public override void Initialize()
    {
        startPosition = transform.position;
        startRotation = transform.rotation;

        characterController = GetComponent<CharacterController>();

        if (agentCamera == null)
            agentCamera = GetComponentInChildren<Camera>();

        if (baseAgentComponent == null)
            baseAgentComponent = GetComponent<BaseAgentComponent>();

        if (baseAgentComponent != null)
        {
            thorAgent = baseAgentComponent.agent;
            if (thorAgent != null)
                thorAgentType = thorAgent.GetType();
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

        Debug.Log(thorAgent != null
            ? "[ThorMLAgent] thorAgent 연결 성공"
            : "[ThorMLAgent] thorAgent 연결 실패");

        Debug.Log($"[ThorMLAgent] startPosition = {startPosition}");
        Debug.Log($"[ThorMLAgent] startRotation = {startRotation.eulerAngles}");
    }

    public override void OnEpisodeBegin()
    {
        episodeCount++;

        if (characterController != null)
            characterController.enabled = false;

        transform.position = startPosition;
        transform.rotation = startRotation;

        if (characterController != null)
            characterController.enabled = true;

        prevTargetDistance = -1f;
        currentStep = 0;

        SelectEpisodeTarget();

        Debug.Log($"[ThorMLAgent] Episode Start #{episodeCount}");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 총 8개
        // 1~2: 에이전트 forward
        sensor.AddObservation(transform.forward.x);
        sensor.AddObservation(transform.forward.z);

        // 3~4: 카메라 forward
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

        // 5~6: 목표 방향
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

        // 7: 목표 거리
        if (agentCamera != null && episodeTarget != null)
        {
            float dist = Vector3.Distance(agentCamera.transform.position, episodeTarget.transform.position);
            sensor.AddObservation(Mathf.Clamp01(dist / 5f));
        }
        else
        {
            sensor.AddObservation(1f);
        }

        // 8: 현재 정면에 목표가 있는지
        SimObjPhysics frontTarget = GetInteractableTarget();
        sensor.AddObservation(
            frontTarget != null &&
            episodeTarget != null &&
            frontTarget.ObjectID == episodeTarget.ObjectID ? 1f : 0f
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

            // 너무 작고 어려운 물체 제외
            if (id.Contains("spoon")) continue;
            if (id.Contains("fork")) continue;
            if (id.Contains("knife")) continue;
            if (id.Contains("pen")) continue;

            candidates.Add(obj);
        }

        if (candidates.Count > 0)
        {
            episodeTarget = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            Debug.Log("[ThorMLAgent] Episode Target = " + episodeTarget.ObjectID);
        }
        else
        {
            episodeTarget = null;
            Debug.LogWarning("[ThorMLAgent] Pickup 가능한 타겟 없음");
        }
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

        Ray ray = new Ray(agentCamera.transform.position, agentCamera.transform.forward);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, interactDistance, ~0, QueryTriggerInteraction.Ignore))
        {
            return hit.collider.GetComponentInParent<SimObjPhysics>();
        }

        return null;
    }

    private GameObject GetHoldingObject()
    {
        if (thorAgent == null || thorAgentType == null) return null;

        try
        {
            MethodInfo method = thorAgentType.GetMethod("WhatAmIHolding", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return null;

            object result = method.Invoke(thorAgent, null);
            return result as GameObject;
        }
        catch
        {
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

    private bool InvokePickupObject(string objectId)
    {
        if (thorAgent == null || thorAgentType == null) return false;

        try
        {
            MethodInfo method = thorAgentType.GetMethod("PickupObject", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return false;

            ParameterInfo[] ps = method.GetParameters();

            if (ps.Length == 3)
            {
                method.Invoke(thorAgent, new object[] { objectId, false, true });
                return true;
            }
            if (ps.Length == 2)
            {
                method.Invoke(thorAgent, new object[] { objectId, false });
                return true;
            }
            if (ps.Length == 1)
            {
                method.Invoke(thorAgent, new object[] { objectId });
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool TryPickupEpisodeTarget()
    {
        if (thorAgent == null || episodeTarget == null) return false;

        if (GetHoldingObject() != null)
            return false;

        SimObjPhysics frontTarget = GetInteractableTarget();
        if (frontTarget == null) return false;

        if (frontTarget.ObjectID != episodeTarget.ObjectID)
            return false;

        bool invoked = InvokePickupObject(frontTarget.ObjectID);
        if (!invoked) return false;

        return IsHoldingEpisodeTarget();
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

        int move = Mathf.FloorToInt(vectorAction[0]);
        int turn = Mathf.FloorToInt(vectorAction[1]);
        int interact = Mathf.FloorToInt(vectorAction[2]);

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

        Vector3 pos = transform.position;
        pos.x = Mathf.Clamp(pos.x, minX, maxX);
        pos.z = Mathf.Clamp(pos.z, minZ, maxZ);
        transform.position = pos;

        AddDenseReward();

        if (interact == 1)
        {
            SimObjPhysics frontTarget = GetInteractableTarget();

            if (frontTarget == null)
            {
                AddReward(-0.002f);
            }
            else if (episodeTarget == null)
            {
                AddReward(pickupFailPenalty);
            }
            else if (frontTarget.ObjectID != episodeTarget.ObjectID)
            {
                AddReward(wrongTargetPenalty);
                Debug.Log("[ThorMLAgent] 잘못된 타겟 시도 | front=" + frontTarget.ObjectID + " / target=" + episodeTarget.ObjectID);
            }
            else if (prevTargetDistance < 0f || prevTargetDistance > pickupTryDistance)
            {
                AddReward(-0.002f);
            }
            else
            {
                bool picked = TryPickupEpisodeTarget();

                if (picked)
                {
                    AddReward(pickupSuccessReward);

                    successCount++;
                    Debug.Log($"[ThorMLAgent] Pickup 성공 | Target={episodeTarget.ObjectID}");
                    Debug.Log($"[ThorMLAgent] SuccessRate = {(float)successCount / episodeCount:F2} ({successCount}/{episodeCount})");
                    Debug.Log($"[ThorMLAgent] Episode Reward = {GetCumulativeReward():F3}");

                    AddReward(successEndReward);
                    EndEpisode();
                    return;
                }
                else
                {
                    AddReward(pickupFailPenalty);
                    Debug.Log("[ThorMLAgent] Pickup 실패 | target은 맞았지만 집지 못함");
                }
            }
        }

        if (IsHoldingEpisodeTarget())
        {
            successCount++;
            Debug.Log($"[ThorMLAgent] Pickup 성공(후처리) | Target={episodeTarget.ObjectID}");
            Debug.Log($"[ThorMLAgent] SuccessRate = {(float)successCount / episodeCount:F2} ({successCount}/{episodeCount})");
            Debug.Log($"[ThorMLAgent] Episode Reward = {GetCumulativeReward():F3}");

            AddReward(successEndReward);
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
        actionsOut[0] = 0f;
        actionsOut[1] = 0f;
        actionsOut[2] = 0f;

        if (Input.GetKey(KeyCode.W)) actionsOut[0] = 1f;
        if (Input.GetKey(KeyCode.S)) actionsOut[0] = 2f;
        if (Input.GetKey(KeyCode.A)) actionsOut[1] = 1f;
        if (Input.GetKey(KeyCode.D)) actionsOut[1] = 2f;
        if (Input.GetKey(KeyCode.E)) actionsOut[2] = 1f;
    }
}