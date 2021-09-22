using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using Mirror;

namespace VNT.PongExample
{
    [DisallowMultipleComponent]
    public class PongBallVNT : VariantNetworkTransformBase
    {
        protected override Transform targetComponent => transform;
        
        protected override void ConstructSyncData(bool fromServer)
        {
            PongBallSyncData syncData = new PongBallSyncData (
                syncPosition ? targetComponent.localPosition : new Vector3?()
            );

            SerializeAndSend<PongBallSyncData>(syncData, fromServer);   
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
}
