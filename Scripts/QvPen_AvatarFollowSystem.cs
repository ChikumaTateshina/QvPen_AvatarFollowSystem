using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon;
using Utilities = VRC.SDKBase.Utilities;

#pragma warning disable IDE0044
#pragma warning disable IDE0090, IDE1006

[AddComponentMenu("QvPen Extension/QvPen Avatar Follow System")]
[DefaultExecutionOrder(100)]
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class QvPen_AvatarFollowSystem : UdonSharpBehaviour
{
    #region Inspector

    [Header("追従設定")]
    [Tooltip("ボーンからこの距離(m)以内に描き終えた線が追従対象になる(既定リーチ)。\n" +
             "特定ボーンだけ変えたい場合は下の Bone Reach Override で上書きできる")]
    [SerializeField] private float attachDistance = 0.6f;

    [Tooltip("ローカルプレイヤー(描いた本人)のボーンも対象に含めるか。\n" +
             "既定は ON。自分のアバターに描いた線もその場で追従する。\n" +
             "※ペンを持つ手に線が貼り付くのが気になる場合は OFF にする")]
    [SerializeField] private bool includeLocalPlayer = true;

    [Tooltip("同時に追従できる最大本数。超えると古いものから自動的に切り離す。\n" +
             "大きくすると同期帯域が増える(1本あたり 72byte)。\n" +
             "VRChat の同期上限(約 49KB)に収まるよう 680 本程度までを目安とする")]
    [SerializeField] private int maxRecords = 511;

    [Tooltip("追従対象ボーンの HumanBodyBones 整数値リスト。空なら主要20ボーンを自動設定。")]
    [SerializeField] private int[] candidateBoneIds;

    [Header("ボーン別の追従開始距離(リーチ)上書き")]
    [Tooltip("リーチを上書きしたいボーンの HumanBodyBones 整数値。下の Bone Reach Overrides と同じ順で対応。\n" +
             "Head(10) はボーン原点(首元)が顔表面から離れているので、ここに 10 を入れリーチを大きめにすると顔に書いた線が頭に付きやすい。")]
    [SerializeField] private int[] boneReachOverrideIds;

    [Tooltip("上の Bone Reach Override Ids に対応する追従開始距離(m)。\n" +
             "選択は「距離 ÷ リーチ」の比が最小のボーンを採用するので、リーチを大きくしたボーンほど遠くの線を引き寄せる。")]
    [SerializeField] private float[] boneReachOverrides;

    [Tooltip("ON にすると、線が追従を開始する際に「どのプレイヤーのどのボーンに距離いくつで付いたか」をログ出力する(ボーン選択の確認・調整用)")]
    [SerializeField] private bool debugLog = false;

    private int[] boneIds = new int[0];

    #endregion

    #region Const

    private const string INK_POOL_ROOT_NAME = "QvPen_Objects";
    private const int RECORD_STRIDE = 6;

    // 同期(RequestSerialization)が失敗したときの再送設定
    private const int MAX_SERIALIZATION_RETRIES = 3;
    private const float RETRY_DELAY_SECONDS = 0.5f;

    #endregion

    #region Runtime state

    private VRCPlayerApi localPlayer;
    private int localPlayerId;
    private bool isEditor;

    private Transform inkPoolRoot;
    private int lastRootChildCount = -1;
    private int rebuildTimer = 0;
    private int registryCleanTimer = 0;

    private Transform[] containerArr = new Transform[0];
    private int[] containerCountArr = new int[0];

    private readonly DataDictionary registry = new DataDictionary();
    private readonly DataDictionary activeSet = new DataDictionary();
    private readonly DataDictionary localRef = new DataDictionary();

    [UdonSynced] private Vector3[] _syncedRecords = new Vector3[0];

    private int rCount = 0;
    private string[] rKey = new string[0];
    private int[] rPenId = new int[0];
    private int[] rInkId = new int[0];
    private int[] rPlayerId = new int[0];
    private int[] rBone = new int[0];
    private Vector3[] rPos0 = new Vector3[0];
    private Quaternion[] rRot0 = new Quaternion[0];

    private VRCPlayerApi[] playersBuf = new VRCPlayerApi[96];
    private readonly DataList pruneBuf = new DataList();

    private int ownerPruneTimer = 0;

    private int serializationRetryCount = 0;
    private bool retryScheduled = false;

    #endregion

    #region Unity / VRChat events

    private void Start()
    {
        localPlayer = Networking.LocalPlayer;
        isEditor = !Utilities.IsValid(localPlayer);
        if (!isEditor)
            localPlayerId = localPlayer.playerId;

        if (candidateBoneIds != null && candidateBoneIds.Length > 0)
            boneIds = candidateBoneIds;
        else
            boneIds = DefaultBoneIds();

        ParseRecords();
    }

    private void Update()
    {
        if (isEditor)
            return;

        if (!Utilities.IsValid(inkPoolRoot))
        {
            var rootGo = GameObject.Find("/" + INK_POOL_ROOT_NAME);
            if (!Utilities.IsValid(rootGo))
                return;

            inkPoolRoot = rootGo.transform;
            lastRootChildCount = -1;
        }

        rebuildTimer++;
        if (inkPoolRoot.childCount != lastRootChildCount || rebuildTimer >= 60)
        {
            RebuildContainers();
            lastRootChildCount = inkPoolRoot.childCount;
            rebuildTimer = 0;
        }

        PollNewInks();

        registryCleanTimer++;
        if (registryCleanTimer >= 300)
        {
            registryCleanTimer = 0;
            CleanRegistry();
        }

        if (Networking.IsOwner(gameObject))
        {
            ownerPruneTimer++;
            if (ownerPruneTimer >= 30)
            {
                ownerPruneTimer = 0;
                OwnerPruneStale();
            }
        }
    }

    private void CleanRegistry()
    {
        var keys = registry.GetKeys();
        for (int i = 0; i < keys.Count; i++)
        {
            if (!keys.TryGetValue(i, TokenType.String, out var k))
                continue;

            if (registry.TryGetValue(k, TokenType.Reference, out var v)
                && Utilities.IsValid((GameObject)v.Reference))
                continue;

            registry.Remove(k);
        }
    }

    public override void PostLateUpdate()
    {
        if (isEditor || rCount == 0)
            return;

        var owner = Networking.IsOwner(gameObject);
        pruneBuf.Clear();

        for (int i = 0; i < rCount; i++)
        {
            var key = rKey[i];
            GameObject go = null;

            if (activeSet.ContainsKey(key))
            {
                if (activeSet.TryGetValue(key, TokenType.Reference, out var t))
                    go = (GameObject)t.Reference;

                if (!Utilities.IsValid(go))
                {
                    activeSet.Remove(key);
                    if (localRef.ContainsKey(key)) localRef.Remove(key);
                    if (registry.ContainsKey(key)) registry.Remove(key);
                    if (owner) pruneBuf.Add(key);
                    continue;
                }
            }
            else
            {
                if (!registry.ContainsKey(key))
                    continue;

                if (registry.TryGetValue(key, TokenType.Reference, out var t))
                    go = (GameObject)t.Reference;

                if (!Utilities.IsValid(go))
                {
                    registry.Remove(key);
                    continue;
                }

                var lr0 = go.GetComponent<LineRenderer>();
                if (!Utilities.IsValid(lr0))
                    continue;

                lr0.useWorldSpace = false;
                activeSet[key] = new DataToken(go);
            }

            var player = VRCPlayerApi.GetPlayerById(rPlayerId[i]);
            if (!Utilities.IsValid(player))
            {
                if (owner) pruneBuf.Add(key);
                continue;
            }

            var bone = (HumanBodyBones)rBone[i];
            var bonePos = player.GetBonePosition(bone);
            if (bonePos == Vector3.zero)
                continue;

            var boneRot = player.GetBoneRotation(bone);

            Vector3 refPos;
            Quaternion refRot;
            if (localRef.TryGetValue(key, TokenType.DataList, out var reft))
            {
                var dl = reft.DataList;
                refPos = new Vector3(GetF(dl, 0), GetF(dl, 1), GetF(dl, 2));
                refRot = new Quaternion(GetF(dl, 3), GetF(dl, 4), GetF(dl, 5), GetF(dl, 6));
            }
            else
            {
                refPos = bonePos;
                refRot = boneRot;
                var dl = new DataList();
                dl.Add(bonePos.x); dl.Add(bonePos.y); dl.Add(bonePos.z);
                dl.Add(boneRot.x); dl.Add(boneRot.y); dl.Add(boneRot.z); dl.Add(boneRot.w);
                localRef[key] = new DataToken(dl);
            }

            var rotD = boneRot * Quaternion.Inverse(refRot);
            var posD = bonePos - rotD * refPos;
            go.transform.SetPositionAndRotation(posD, rotD);
        }

        if (owner && pruneBuf.Count > 0)
            RemoveRecordsByKeys(pruneBuf);
    }

    public override void OnDeserialization()
    {
        ParseRecords();
    }

    public override void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
    {
        if (result.success)
        {
            serializationRetryCount = 0;

            if (debugLog)
                Debug.Log("[QvPen_AvatarFollowSystem] serialized " + result.byteCount
                    + " bytes (" + rCount + " records)");
            return;
        }

        // 送信失敗。オーナーでなくなっていれば新オーナーが送り直すので再送しない
        if (!Networking.IsOwner(gameObject))
        {
            serializationRetryCount = 0;
            return;
        }

        serializationRetryCount++;

        // 規定回数を超えて失敗が続く場合はデータ量超過とみなし、古い記録を間引く
        if (serializationRetryCount > MAX_SERIALIZATION_RETRIES)
        {
            serializationRetryCount = 0;
            if (!TrimOldestRecords())
                return;
        }

        if (retryScheduled)
            return;

        // 間引き直後は serializationRetryCount が 0 に戻っているため下限を 1 にする
        var attempt = serializationRetryCount < 1 ? 1 : serializationRetryCount;

        retryScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_RetrySerialization), RETRY_DELAY_SECONDS * attempt);
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (Networking.IsOwner(gameObject))
            OwnerPruneStale();
    }

    #endregion

    #region Ink polling / detection

    private void RebuildContainers()
    {
        var total = 0;
        var poolCount = inkPoolRoot.childCount;
        for (int i = 0; i < poolCount; i++)
        {
            var pool = inkPoolRoot.GetChild(i);
            if (Utilities.IsValid(pool))
                total += pool.childCount;
        }

        containerArr = new Transform[total];
        containerCountArr = new int[total];
        var k = 0;
        for (int i = 0; i < poolCount; i++)
        {
            var pool = inkPoolRoot.GetChild(i);
            if (!Utilities.IsValid(pool))
                continue;

            var sub = pool.childCount;
            for (int j = 0; j < sub && k < total; j++)
            {
                var container = pool.GetChild(j);
                if (!Utilities.IsValid(container))
                    continue;

                containerArr[k] = container;
                containerCountArr[k] = container.childCount;
                k++;
            }
        }
    }

    private void PollNewInks()
    {
        for (int c = 0; c < containerArr.Length; c++)
        {
            var t = containerArr[c];
            if (!Utilities.IsValid(t))
                continue;

            var now = t.childCount;
            var last = containerCountArr[c];

            if (now > last)
            {
                for (int i = last; i < now; i++)
                {
                    var child = t.GetChild(i);
                    if (Utilities.IsValid(child))
                        InspectInk(child);
                }
            }

            containerCountArr[c] = now;
        }
    }

    private void InspectInk(Transform ink)
    {
        if (ink.childCount < 2)
            return;

        var go = ink.gameObject;
        var lr = go.GetComponent<LineRenderer>();
        if (!Utilities.IsValid(lr))
            return;

        var idHolder = ink.GetChild(1);
        if (!Utilities.IsValid(idHolder))
            return;

        var penId = Vector3ToInt32(idHolder.localPosition);
        var inkId = Vector3ToInt32(idHolder.localScale);
        var ownerId = EulerAnglesToPlayerId(idHolder.localEulerAngles);

        var key = penId + ":" + inkId;
        if (registry.ContainsKey(key))
            return;

        registry[key] = new DataToken(go);

        if (ownerId == localPlayerId)
            TryAttach(go, lr, penId, inkId);
    }

    private void TryAttach(GameObject go, LineRenderer lr, int penId, int inkId)
    {
        var n = lr.positionCount;
        if (n <= 0)
            return;

        var pts = new Vector3[n];
        lr.GetPositions(pts);

        var sum = Vector3.zero;
        for (int i = 0; i < n; i++)
            sum += pts[i];
        var centroid = sum / n;

        var count = VRCPlayerApi.GetPlayerCount();
        if (count > playersBuf.Length)
            count = playersBuf.Length;
        VRCPlayerApi.GetPlayers(playersBuf);

        var bestRatio = 1f;
        var bestDist = 0f;
        VRCPlayerApi bestPlayer = null;
        var bestBone = HumanBodyBones.Hips;
        var bestPos = Vector3.zero;
        var found = false;

        for (int pi = 0; pi < count; pi++)
        {
            var p = playersBuf[pi];
            if (!Utilities.IsValid(p))
                continue;
            if (!includeLocalPlayer && p.playerId == localPlayerId)
                continue;

            for (int bi = 0; bi < boneIds.Length; bi++)
            {
                var boneId = boneIds[bi];
                var rootPos = p.GetBonePosition((HumanBodyBones)boneId);
                if (rootPos == Vector3.zero)
                    continue;

                var reach = GetBoneReach(boneId);
                if (reach <= 0f)
                    continue;

                var childId = ChildBone(boneId);
                var hasChild = false;
                var childPos = Vector3.zero;
                var hops = 0;
                while (childId >= 0 && hops < 4)
                {
                    var cp = p.GetBonePosition((HumanBodyBones)childId);
                    if (cp != Vector3.zero) { childPos = cp; hasChild = true; break; }
                    childId = ChildBone(childId);
                    hops++;
                }

                var d = hasChild
                    ? Vector3.Distance(centroid, ClosestPointOnSegment(centroid, rootPos, childPos))
                    : Vector3.Distance(centroid, rootPos);

                var ratio = d / reach;
                if (ratio < bestRatio)
                {
                    bestRatio = ratio;
                    bestDist = d;
                    bestPlayer = p;
                    bestBone = (HumanBodyBones)boneId;
                    bestPos = rootPos;
                    found = true;
                }
            }
        }

        if (!found)
            return;

        var bestRot = bestPlayer.GetBoneRotation(bestBone);

        if (debugLog)
            Debug.Log("[QvPen_AvatarFollowSystem] attach ink " + inkId
                + " -> player '" + bestPlayer.displayName + "'"
                + " bone#" + (int)bestBone + " dist=" + bestDist + "m"
                + " (reach=" + GetBoneReach((int)bestBone) + ", ratio=" + bestRatio + ")");

        AddRecord(penId, inkId, bestPlayer.playerId, (int)bestBone, bestPos, bestRot);
    }

    #endregion

    #region Records (sync)

    private void AddRecord(int penId, int inkId, int playerId, int boneInt, Vector3 pos0, Quaternion rot0)
    {
        var key = penId + ":" + inkId;
        for (int i = 0; i < rCount; i++)
            if (rKey[i] == key)
                return;

        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(localPlayer, gameObject);

        var newCount = rCount + 1;
        var drop = newCount > maxRecords ? newCount - maxRecords : 0;
        var finalCount = newCount - drop;

        var arr = new Vector3[finalCount * RECORD_STRIDE];
        var w = 0;
        for (int i = drop; i < rCount; i++)
        {
            WriteRecord(arr, w, rPenId[i], rInkId[i], rPlayerId[i], rBone[i], rPos0[i], rRot0[i]);
            w++;
        }
        WriteRecord(arr, w, penId, inkId, playerId, boneInt, pos0, rot0);

        _syncedRecords = arr;
        ParseRecords();
        RequestSync();
    }

    private void OwnerPruneStale()
    {
        if (rCount == 0)
            return;

        pruneBuf.Clear();
        for (int i = 0; i < rCount; i++)
        {
            var player = VRCPlayerApi.GetPlayerById(rPlayerId[i]);
            if (!Utilities.IsValid(player))
            {
                pruneBuf.Add(rKey[i]);
                continue;
            }

            if (registry.ContainsKey(rKey[i]))
            {
                GameObject go = null;
                if (registry.TryGetValue(rKey[i], TokenType.Reference, out var t))
                    go = (GameObject)t.Reference;

                if (!Utilities.IsValid(go))
                {
                    pruneBuf.Add(rKey[i]);
                    registry.Remove(rKey[i]);
                }
            }
        }

        if (pruneBuf.Count > 0)
            RemoveRecordsByKeys(pruneBuf);
    }

    private void RemoveRecordsByKeys(DataList keys)
    {
        if (rCount == 0 || keys.Count == 0)
            return;

        var kill = new DataDictionary();
        for (int i = 0; i < keys.Count; i++)
            if (keys.TryGetValue(i, TokenType.String, out var k))
                kill[k.String] = true;

        var keep = 0;
        for (int i = 0; i < rCount; i++)
            if (!kill.ContainsKey(rKey[i]))
                keep++;

        var arr = new Vector3[keep * RECORD_STRIDE];
        var w = 0;
        for (int i = 0; i < rCount; i++)
        {
            if (kill.ContainsKey(rKey[i]))
                continue;
            WriteRecord(arr, w, rPenId[i], rInkId[i], rPlayerId[i], rBone[i], rPos0[i], rRot0[i]);
            w++;
        }

        _syncedRecords = arr;
        ParseRecords();

        if (Networking.IsOwner(gameObject))
            RequestSync();
    }

    private void ParseRecords()
    {
        var len = _syncedRecords == null ? 0 : _syncedRecords.Length;
        var cnt = len / RECORD_STRIDE;

        var keys = new string[cnt];
        var penIds = new int[cnt];
        var inkIds = new int[cnt];
        var playerIds = new int[cnt];
        var bones = new int[cnt];
        var pos0s = new Vector3[cnt];
        var rot0s = new Quaternion[cnt];

        var keySet = new DataDictionary();

        for (int i = 0; i < cnt; i++)
        {
            var b = i * RECORD_STRIDE;
            var penId = Vector3ToInt32(_syncedRecords[b + 0]);
            var inkId = Vector3ToInt32(_syncedRecords[b + 1]);
            var info = _syncedRecords[b + 2];

            penIds[i] = penId;
            inkIds[i] = inkId;
            playerIds[i] = Mathf.RoundToInt(info.x);
            bones[i] = Mathf.RoundToInt(info.y);
            pos0s[i] = _syncedRecords[b + 3];
            var q = _syncedRecords[b + 4];
            var qw = _syncedRecords[b + 5].x;
            rot0s[i] = new Quaternion(q.x, q.y, q.z, qw);

            var key = penId + ":" + inkId;
            keys[i] = key;
            keySet[key] = true;
        }

        var activeKeys = activeSet.GetKeys();
        for (int i = 0; i < activeKeys.Count; i++)
        {
            if (!activeKeys.TryGetValue(i, TokenType.String, out var k))
                continue;
            if (!keySet.ContainsKey(k.String))
            {
                activeSet.Remove(k.String);
                if (localRef.ContainsKey(k.String)) localRef.Remove(k.String);
            }
        }

        rKey = keys;
        rPenId = penIds;
        rInkId = inkIds;
        rPlayerId = playerIds;
        rBone = bones;
        rPos0 = pos0s;
        rRot0 = rot0s;
        rCount = cnt;
    }

    private static void WriteRecord(Vector3[] arr, int recIndex, int penId, int inkId, int playerId, int boneInt, Vector3 pos0, Quaternion rot0)
    {
        var b = recIndex * RECORD_STRIDE;
        arr[b + 0] = Int32ToVector3(penId);
        arr[b + 1] = Int32ToVector3(inkId);
        arr[b + 2] = new Vector3(playerId, boneInt, 0f);
        arr[b + 3] = pos0;
        arr[b + 4] = new Vector3(rot0.x, rot0.y, rot0.z);
        arr[b + 5] = new Vector3(rot0.w, 0f, 0f);
    }

    #endregion

    #region Sync stability

    /// <summary>
    /// オーナーとして同期を要求する。再送カウンタをリセットしてから発行する。
    /// </summary>
    private void RequestSync()
    {
        serializationRetryCount = 0;
        RequestSerialization();
    }

    /// <summary>
    /// OnPostSerialization から遅延実行される再送処理。
    /// </summary>
    public void _RetrySerialization()
    {
        retryScheduled = false;

        if (!Networking.IsOwner(gameObject))
        {
            serializationRetryCount = 0;
            return;
        }

        RequestSerialization();
    }

    /// <summary>
    /// 送信失敗が続く場合に、古い記録から 1/4(最低 1 本)を切り離してデータ量を減らす。
    /// これ以上減らせない場合は false を返す。
    /// </summary>
    private bool TrimOldestRecords()
    {
        if (rCount == 0)
            return false;

        var drop = rCount / 4;
        if (drop < 1)
            drop = 1;

        var keep = rCount - drop;
        var arr = new Vector3[keep * RECORD_STRIDE];
        var w = 0;
        for (int i = drop; i < rCount; i++)
        {
            WriteRecord(arr, w, rPenId[i], rInkId[i], rPlayerId[i], rBone[i], rPos0[i], rRot0[i]);
            w++;
        }

        _syncedRecords = arr;
        ParseRecords();

        if (debugLog)
            Debug.LogWarning("[QvPen_AvatarFollowSystem] serialization keeps failing;"
                + " dropped " + drop + " oldest record(s), " + keep + " remain."
                + " Consider lowering Max Records.");

        return true;
    }

    #endregion

    #region Public API

    public void _DetachAll()
    {
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(localPlayer, gameObject);

        _syncedRecords = new Vector3[0];
        ParseRecords();
        RequestSync();
    }

    #endregion

    #region Utilities

    private static float GetF(DataList dl, int i)
        => dl.TryGetValue(i, TokenType.Float, out var t) ? t.Float : 0f;

    private static int ChildBone(int boneId)
    {
        switch (boneId)
        {
            case 7:  return 8;
            case 8:  return 54;
            case 54: return 9;
            case 11: return 13;
            case 12: return 14;
            case 13: return 15;
            case 14: return 16;
            case 15: return 17;
            case 16: return 18;
            case 1:  return 3;
            case 2:  return 4;
            case 3:  return 5;
            case 4:  return 6;
            default: return -1;
        }
    }

    private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var denom = Vector3.Dot(ab, ab);
        if (denom < 1e-8f)
            return a;
        var t = Vector3.Dot(p - a, ab) / denom;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        return a + ab * t;
    }

    private float GetBoneReach(int boneId)
    {
        if (boneReachOverrideIds != null)
        {
            for (int i = 0; i < boneReachOverrideIds.Length; i++)
            {
                if (boneReachOverrideIds[i] == boneId)
                {
                    if (boneReachOverrides != null && i < boneReachOverrides.Length)
                        return boneReachOverrides[i];
                    break;
                }
            }
        }
        return attachDistance;
    }

    private static Vector3 Int32ToVector3(int v)
        => new Vector3((v >> 24) & 0x00ff, (v >> 12) & 0x0fff, v & 0x0fff);

    private static int Vector3ToInt32(Vector3 v)
        => ((int)v.x & 0x00ff) << 24 | ((int)v.y & 0x0fff) << 12 | ((int)v.z & 0x0fff);

    private const int PIDB = 360;
    private const float PIDD = PIDB / 90f;
    private static int EulerAnglesToPlayerId(Vector3 v)
    {
        v *= PIDD;
        return Mathf.RoundToInt(v.x)
            + Mathf.RoundToInt(v.y) * PIDB
            + Mathf.RoundToInt(v.z) * (PIDB * PIDB);
    }

    private static int[] DefaultBoneIds()
    {
        return new int[]
        {
            (int)HumanBodyBones.Head,
            (int)HumanBodyBones.LeftHand,
            (int)HumanBodyBones.RightHand,
            (int)HumanBodyBones.LeftFoot,
            (int)HumanBodyBones.RightFoot,
            (int)HumanBodyBones.LeftLowerArm,
            (int)HumanBodyBones.RightLowerArm,
            (int)HumanBodyBones.LeftLowerLeg,
            (int)HumanBodyBones.RightLowerLeg,
            (int)HumanBodyBones.LeftUpperArm,
            (int)HumanBodyBones.RightUpperArm,
            (int)HumanBodyBones.LeftUpperLeg,
            (int)HumanBodyBones.RightUpperLeg,
            (int)HumanBodyBones.LeftShoulder,
            (int)HumanBodyBones.RightShoulder,
            (int)HumanBodyBones.Neck,
            (int)HumanBodyBones.UpperChest,
            (int)HumanBodyBones.Chest,
            (int)HumanBodyBones.Spine,
            (int)HumanBodyBones.Hips,
        };
    }

    #endregion
}
