using Mirror;
using System;

namespace VNT 
{
    //Make a dedicated network writer for NT data. Prevents potential clashes
    //or overwriting when using PoolNetworkWriter.

    public sealed class VNTStaticWriter : NetworkWriter, IDisposable
    {
        public void Dispose() => VNTWriter.Reset();
    }
    public static class VNTWriter
    {
        private static VNTStaticWriter writer = new VNTStaticWriter();
        public static VNTStaticWriter GetWriter()
        {
            return writer;
        }

        public static void Reset()
        {
            writer.Reset();
        }
    }
}