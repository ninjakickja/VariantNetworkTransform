using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Mirror;

namespace VNT.PongExample 
{
    //In the Pong game we only need to sync position of the ball,
    //and only y value of the racket position.
    
    [Serializable]
    public struct PongBallSyncData
    {
        public Vector3? position;
        
        public PongBallSyncData(Vector3? position)
        {
            this.position = position;
        }        
    }

    [Serializable]
    public struct PongRacketSyncData
    {
        public short y;

        public PongRacketSyncData(short value)
        {
            this.y = value;
        }
    }

    public static class PongCustomReaderWriter
    {
        public static void WriteBallSyncData(this NetworkWriter writer, PongBallSyncData syncData)
        {
            writer.WriteVector3Nullable(syncData.position);
        }

        public static PongBallSyncData ReadBallSyncData(this NetworkReader reader)
        {
            return new PongBallSyncData(
                reader.ReadVector3Nullable()
            );
        }

        public static void WriteRacketSyncData(this NetworkWriter writer, PongRacketSyncData syncData)        
        {
            writer.WriteShort(syncData.y);
        }

        public static PongRacketSyncData ReadRacketSyncData(this NetworkReader reader)
        {
            return new PongRacketSyncData(
                reader.ReadShort()
            );
        }
    }
}
