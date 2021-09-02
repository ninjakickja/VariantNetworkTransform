using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using Mirror;
using VNT;

[DisallowMultipleComponent]
public class PongBallVNT : VariantNetworkTransformBase
{
    protected override Transform targetComponent => transform;

    protected override ArraySegment<byte> ConstructSyncData()
    {
        PongBallSyncData pongBallSyncData = new PongBallSyncData (
            syncPosition ? targetComponent.localPosition : new Vector3?()
        );

        using (VNTStaticWriter writer = VNTWriter.GetWriter())
        {
            writer.Write<PongBallSyncData>(pongBallSyncData);
            return writer.ToArraySegment();
        }
    }

    protected override void DeconstructSyncData(ArraySegment<byte> receivedPayload, out Vector3? position, out Quaternion? rotation, out Vector3? scale)
    {
        using (PooledNetworkReader reader = NetworkReaderPool.GetReader(receivedPayload))
        {
            PongBallSyncData syncData = reader.Read<PongBallSyncData>();

            position = syncData.position;
            rotation = null;
            scale = null;
        }        
    }
}
