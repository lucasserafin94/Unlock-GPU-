namespace UnlockGpu.Core.Bench;

/// <summary>Embedded HLSL (compute shader model 5.0). Both shaders use a grid-stride loop so one dispatch of
/// <see cref="Groups"/> x <see cref="ThreadsPerGroup"/> threads covers a buffer of any size.</summary>
internal static class ComputeShaders
{
    public const uint ThreadsPerGroup = 256;
    public const uint Groups = 4096;
    public const uint Threads = Groups * ThreadsPerGroup;

    /// <summary>Benchmark: every element is read, put through <c>Iter</c> x 4 independent FMAs and written back,
    /// so a dispatch mixes ALU load (core clock) with a full read+write sweep of the buffer (memory clock).
    /// cbuffer: A = element count, B = stride (total threads), C = FMA iterations, D = 1 to seed the buffer.</summary>
    public const string Bench = """
        cbuffer Params : register(b0) { uint Count; uint Stride; uint Iter; uint Init; };
        RWStructuredBuffer<float> Data : register(u0);

        [numthreads(256, 1, 1)]
        void Bench(uint3 id : SV_DispatchThreadID)
        {
            for (uint i = id.x; i < Count; i += Stride)
            {
                float v = Init != 0 ? (float)(i & 1023u) * 0.001f : Data[i];
                float a = v, b = v + 1.0f, c = v + 2.0f, d = v + 3.0f;
                [loop]
                for (uint k = 0; k < Iter; k++)
                {
                    a = mad(a, 0.9999f, 0.0001f);
                    b = mad(b, 0.9999f, 0.0002f);
                    c = mad(c, 0.9999f, 0.0003f);
                    d = mad(d, 0.9999f, 0.0004f);
                }
                Data[i] = (a + b + c + d) * 0.25f;
            }
        }
        """;

    /// <summary>VRAM fill. Must match <see cref="VramPatterns.Expected"/> bit for bit.
    /// cbuffer: A = word count, B = stride, C = global word address of element 0 (mod 2^32), D = pattern.</summary>
    public const string Fill = """
        cbuffer Params : register(b0) { uint Count; uint Stride; uint Base; uint Pattern; };
        RWStructuredBuffer<uint> Data : register(u0);

        uint Expected(uint a)
        {
            if (Pattern == 0u) { uint x = a * 0x9E3779B1u; return x ^ (x >> 16); }
            uint alt = (a & 1u) != 0u ? 0xAAAAAAAAu : 0x55555555u;
            return Pattern == 1u ? alt : ~alt;
        }

        [numthreads(256, 1, 1)]
        void Fill(uint3 id : SV_DispatchThreadID)
        {
            for (uint i = id.x; i < Count; i += Stride)
                Data[i] = Expected(Base + i);
        }
        """;
}
