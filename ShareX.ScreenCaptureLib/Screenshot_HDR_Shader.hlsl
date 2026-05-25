// File: ShareX.ScreenCaptureLib/Screenshot_HDR_Shader.hlsl
//
// HDR Tonemap Compute Shader  (cs_5_0)
//
// Converts a DXGI Desktop Duplication HDR texture (RGBA16F scRGB or
// R10G10B10A2 HDR10/PQ) to 8-bit sRGB BGRA, applying:
//   - Per-monitor SDR white normalisation (scRGB path)
//   - BT.2390-style highlight tonemap
//   - Linear -> sRGB gamma encode
//   - Display rotation correction (texRotation 1-4)
//
// Output format: DXGI_FORMAT_R8G8B8A8_UNORM
// Memory layout of each output texel: [B, G, R, A]
//   (float4(b,g,r,a) written to R8G8B8A8_UNORM puts B at byte 0,
//   matching GDI+ Format32bppArgb for direct Buffer.MemoryCopy.)
//
// Logical pixel (lx, ly) -> physical texture coord (tx, ty):
//   IDENTITY (1): tx = srcOffsetX + lx,          ty = srcOffsetY + ly
//   ROTATE90  (2): tx = srcOffsetY + ly,          ty = texHeight - srcOffsetX - 1 - lx
//   ROTATE180 (3): tx = texWidth  - srcOffsetX - 1 - lx, ty = texHeight - srcOffsetY - 1 - ly
//   ROTATE270 (4): tx = texWidth  - srcOffsetY - 1 - ly, ty = srcOffsetX + lx
//
// Compile: fxc /nologo /T cs_5_0 /E CSMain /Fo HDRTonemap.cso Screenshot_HDR_Shader.hlsl

// X3571: pow(f,e) — inputs are always >= 0 via max()/saturate(); warning is a false positive.
#pragma warning(disable: 3571)

Texture2D<float4>   InputTex  : register(t0);
RWTexture2D<float4> OutputTex : register(u0);

cbuffer Params : register(b0)
{
    // Register 0 (bytes 0-15)
    float  normScale;    // SDR white normalisation: 80.0 / sdrWhiteNits
    uint   inputFormat;  // 0 = RGBA16F (scRGB), 1 = R10G10B10A2 (HDR10/PQ)
    uint   srcOffsetX;   // logSrcX: logical source X within the monitor rect
    uint   srcOffsetY;   // logSrcY: logical source Y within the monitor rect

    // Register 1 (bytes 16-31)
    uint   copyWidth;    // logCopyW: output width  (logical)
    uint   copyHeight;   // logCopyH: output height (logical)
    uint   texRotation;  // DXGI_MODE_ROTATION value: 1=IDENTITY, 2=90CW, 3=180, 4=270CW
    uint   texWidth;     // physical texture width

    // Register 2 (bytes 32-47)
    uint   texHeight;    // physical texture height
    uint   _pad0;
    uint   _pad1;
    uint   _pad2;
};

// ---------------------------------------------------------------------------
// ST.2084 (PQ) Electro-Optical Transfer Function
// Input:  N  in [0, 1] (normalised PQ signal)
// Output: absolute luminance in nits [0, 10000]
// ---------------------------------------------------------------------------
float PQ_EOTF(float N)
{
    const float m1 = 0.1593017578125f;
    const float m2 = 78.84375f;
    const float c1 = 0.8359375f;
    const float c2 = 18.8515625f;
    const float c3 = 18.6875f;

    float Np  = pow(max(N, 0.0f), 1.0f / m2);
    float num = max(Np - c1, 0.0f);
    float den = c2 - c3 * Np;
    if (den <= 0.0f) return 0.0f;
    return pow(num / den, 1.0f / m1) * 10000.0f;
}

// ---------------------------------------------------------------------------
// BT.2390-style luminance-based highlight tonemap.
// SDR content (luminance <= 1.0) passes through unchanged.
// Highlights above 1.0 are soft-compressed, capped at 1.5.
// ---------------------------------------------------------------------------
void TonemapBT2390(inout float r, inout float g, inout float b)
{
    float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
    if (lum > 1.0f)
    {
        float excess     = lum - 1.0f;
        float compressed = 1.0f + excess / (1.0f + excess);
        compressed       = min(compressed, 1.5f);
        float scale      = compressed / lum;
        r *= scale;
        g *= scale;
        b *= scale;
    }
    r = saturate(r);
    g = saturate(g);
    b = saturate(b);
}

// ---------------------------------------------------------------------------
// Linear -> sRGB gamma encode
// ---------------------------------------------------------------------------
float LinearToSRGB(float x)
{
    x = saturate(x);
    return (x <= 0.0031308f) ? (x * 12.92f) : (1.055f * pow(x, 1.0f / 2.4f) - 0.055f);
}

// ---------------------------------------------------------------------------
// Main entry point
// ---------------------------------------------------------------------------
[numthreads(8, 8, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint lx = DTid.x;
    uint ly = DTid.y;

    if (lx >= copyWidth || ly >= copyHeight)
        return;

    // Map logical pixel (lx, ly) to physical texture coordinate (tx, ty).
    // DXGI Desktop Duplication returns the texture in physical (pre-rotation)
    // orientation; we undo the display rotation here so the output is always
    // in the logical (virtual desktop) orientation.
    uint tx, ty;
    switch (texRotation)
    {
        case 2: // ROTATE90  (90° CW display rotation)
            tx = srcOffsetY + ly;
            ty = texHeight - srcOffsetX - 1u - lx;
            break;
        case 3: // ROTATE180
            tx = texWidth  - srcOffsetX - 1u - lx;
            ty = texHeight - srcOffsetY - 1u - ly;
            break;
        case 4: // ROTATE270 (270° CW display rotation)
            tx = texWidth  - srcOffsetY - 1u - ly;
            ty = srcOffsetX + lx;
            break;
        default: // 1 = IDENTITY (and any unexpected value)
            tx = srcOffsetX + lx;
            ty = srcOffsetY + ly;
            break;
    }

    float4 rgba = InputTex[uint2(tx, ty)];
    float  r    = rgba.r;
    float  g    = rgba.g;
    float  b    = rgba.b;
    float  a    = rgba.a;

    if (inputFormat == 0u)
    {
        // -----------------------------------------------------------------
        // RGBA16F / scRGB path
        // Values are linear, scene-referred (> 1.0 is valid for HDR).
        // Normalise by SDR white level, tonemap, then gamma-encode.
        // -----------------------------------------------------------------
        r = max(r * normScale, 0.0f);
        g = max(g * normScale, 0.0f);
        b = max(b * normScale, 0.0f);

        TonemapBT2390(r, g, b);

        r = LinearToSRGB(r);
        g = LinearToSRGB(g);
        b = LinearToSRGB(b);
        a = saturate(a);
    }
    else
    {
        // -----------------------------------------------------------------
        // R10G10B10A2 / HDR10 path
        // Hardware decodes R10G10B10A2_UNORM to float4 in [0,1].
        // Those normalised values are PQ-encoded in BT.2020 gamut.
        // Decode PQ -> linear nits, gamut convert BT.2020 -> BT.709,
        // normalise to [0,1] relative to 80 nits SDR white,
        // tonemap, then gamma-encode.
        // -----------------------------------------------------------------
        float rN = PQ_EOTF(r) / 80.0f;
        float gN = PQ_EOTF(g) / 80.0f;
        float bN = PQ_EOTF(b) / 80.0f;

        // BT.2020 -> BT.709 colour matrix
        r = max( 1.6605f * rN - 0.5877f * gN - 0.0728f * bN, 0.0f);
        g = max(-0.1246f * rN + 1.1330f * gN - 0.0084f * bN, 0.0f);
        b = max(-0.0182f * rN - 0.1006f * gN + 1.1187f * bN, 0.0f);

        TonemapBT2390(r, g, b);

        r = LinearToSRGB(r);
        g = LinearToSRGB(g);
        b = LinearToSRGB(b);
        a = 1.0f;
    }

    // Write as float4(b, g, r, a) to DXGI_FORMAT_R8G8B8A8_UNORM.
    // R8G8B8A8_UNORM memory layout: [R=b, G=g, B=r, A=a] -> bytes [B, G, R, A].
    // This matches GDI+ Format32bppArgb memory layout, enabling direct memcopy.
    OutputTex[uint2(lx, ly)] = float4(b, g, r, a);
}
