using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using Mirror;
using VNT;

[DisallowMultipleComponent]
public class PongRacketVNT : VariantNetworkTransformBase
{
    protected override Transform targetComponent => transform;

    protected override ArraySegment<byte> ConstructSyncData()
    {
        //We try to deconstruct the y value of the racket position to 3 decimals precision
        //This is because we already know (game specific) that the y value do not go beyond
        //-13.xx or 13.xx. So we can have 3 d.p. precision and still be within a short.
        //Need to add checks to ensure y does not go above/below max/min of a short.
        short compressedY = (short)Decimal.Truncate((decimal)targetComponent.localPosition.y * 1000);
        PongRacketSyncData pongRacketSyncData = new PongRacketSyncData (
            compressedY
        );

        using (VNTStaticWriter writer = VNTWriter.GetWriter())
        {
            writer.Write<PongRacketSyncData>(pongRacketSyncData);
            return writer.ToArraySegment();
        }
    }

    protected override void DeconstructSyncData(ArraySegment<byte> receivedPayload, out Vector3? position, out Quaternion? rotation, out Vector3? scale)
    {
        using (PooledNetworkReader reader = NetworkReaderPool.GetReader(receivedPayload))
        {
            PongRacketSyncData syncData = reader.Read<PongRacketSyncData>();

            position = new Vector3(targetComponent.localPosition.x, ((float)syncData.y / 1000), targetComponent.localPosition.z);
            rotation = null;
            scale = null;
        }        
    }
}
