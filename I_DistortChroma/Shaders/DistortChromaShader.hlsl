// ============================================================================
//  DistortChromaShader.hlsl  (v3 : クロップ / 不透明度モード / WB 切替 対応)
//  Sapphire S_DistortChroma 相当を目標にした高品質ディストートクロマ
//
//  t0 : Source (歪ませる画像)
//  t1 : Lens   (歪み方向を決めるバンプマップ。省略時は Source 自身)
//  t2 : Matte  (歪み量をスケールするマスク。省略時は無効)
//
//  v2 : タイル非依存 (ラップ/マスクは SCENE_POSITION 基準) / 勾配ノイズ対策
//  v3 : ・Crop (Top/Bottom/Left/Right)
//         ImgMinTap.xy / ImgSize.xy を「クロップ後の矩形」として受け取る。
//         クロップ外の画素は、Wrap が Tile/Reflect ならクロップ矩形の
//         折り返しで埋め、None なら透明にする。
//       ・Opacity (Normal / All Opaque / As Premult)
//       ・ホワイトバランス (三角形重み合成 + ON で合計を白に。詳細は「スペクトル色の合成とホワイトバランス」のコメント)
//       ・ddx/ddy をすべて分岐の前へ移動（早期 return の後で微分しない）
// ============================================================================

Texture2D SourceTexture : register(t0);
SamplerState SourceSampler : register(s0)
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

Texture2D LensTexture : register(t1);
SamplerState LensSampler : register(s1)
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

Texture2D MatteTexture : register(t2);
SamplerState MatteSampler : register(s2)
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

cbuffer Constants : register(b0)
{
    // 歪み量（ピクセル）: Amount * AmountRelX / Y
    float2 Amount;

    // スペクトル両端の歪み量
    float WarpRed;
    float WarpBlue;

    // サンプリング数(3-100) / 歪み方向の回転(rad) / マット(0=無効,1=有効,2=反転) / マットの使い方(0=Luma,1=Alpha)
    float Steps;
    float Rotate;
    float HasMatte;
    float MatteUse;

    float4 Color1;
    float4 Color2;
    float4 Color3;

    // 適応フィルタ(0/1) / WrapX(0=Black,1=Tile,2=Reflect) / WrapY / 勾配感度
    float UseFilter;
    float WrapX;
    float WrapY;
    float Bump;

    // xy = 処理対象矩形の左上シーン画素座標（クロップ適用後。タイル不変の絶対基準）
    // z  = 勾配差分タップ幅 tap [px]（= max(1, BlurLens)）
    // w  = 勾配ノイズフロア [luma/px]（= 0.75 / (255 * tap)）
    float4 ImgMinTap;
    // xy = 処理対象矩形のサイズ [px]（クロップ適用後。0 以下 = 境界不明 → ラップ/マスク無効）
    float4 ImgSize;

    // --- v3 追加 -----------------------------------------------------------
    // x = ホワイトバランス(0/1)
    // y = 不透明度モード(0=Normal, 1=All Opaque, 2=As Premult)
    // z = クロップ有効(0/1)  ※C# 側の入力矩形計算専用。シェーダー内では未使用
    // w = 予約
    float4 Extra;
};

// ----------------------------------------------------------------------------
//  スペクトル色の合成とホワイトバランス（v3.1）
//
//  スペクトル位置 t の色は、3 色それぞれの「三角形の重み」で線形補間する。
//      w1 = max(1-2t, 0), w2 = 1-|2t-1|, w3 = max(2t-1, 0)
//  さらに各色の重みを、全ステップでの合計が 1 になるよう正規化する。
//      col(t) = C1*w1/H1 + C2*w2/H2 + C3*w3/H3    (Hj = Σ_t wj)
//  すると未歪み部の結果は (Color1 + Color2 + Color3) になる。
//
//  WB OFF : Color1〜3 をそのまま使う。合計が白でない色設定では、
//           未歪み部にも色が乗る / 明るさが変わる。
//  WB ON  : 各色をチャンネルごとの合計で割り、合計を白にする。
//           未歪み部は元の色のまま。
//
//  既定の赤・緑・青は合計がすでに白なので ON/OFF で同じ見た目になる
//  （従来の見た目とも一致）。
// ----------------------------------------------------------------------------

static const float3 LUMA_WEIGHT = float3(0.2126, 0.7152, 0.0722);

float Luminance(float3 c)
{
    return dot(c, LUMA_WEIGHT);
}

// 負の数でも正しく動く剰余
float ModP(float x, float y)
{
    return x - y * floor(x / y);
}

// ---------------------------------------------------------------------------
// シーン画素空間での座標ラップ / 可視マスク
//   p   : 参照したいシーン画素座標
//   vis : 0=枠外(Black 選択時), 1=可視
//   sgn : 反射で折り返された軸は -1（勾配の向き反転用）、それ以外は +1
// ---------------------------------------------------------------------------
float2 WrapPixelS(float2 p, out float vis, out float2 sgn)
{
    vis = 1.0;
    sgn = float2(1.0, 1.0);

    if (ImgSize.x <= 0.0 || ImgSize.y <= 0.0)
        return p; // 境界不明（入力未接続など）は素通し

    float2 d = p - ImgMinTap.xy;

    // --- X ---
    if (WrapX > 1.5)
    {
        float w2 = 2.0 * ImgSize.x;
        float t = ModP(d.x, w2);
        sgn.x = (t > ImgSize.x) ? -1.0 : 1.0;
        d.x = min(t, w2 - t);
    }
    else if (WrapX > 0.5)
    {
        d.x = ModP(d.x, ImgSize.x);
    }
    else
    {
        vis *= (d.x >= 0.0 && d.x <= ImgSize.x) ? 1.0 : 0.0;
    }

    // --- Y ---
    if (WrapY > 1.5)
    {
        float h2 = 2.0 * ImgSize.y;
        float t = ModP(d.y, h2);
        sgn.y = (t > ImgSize.y) ? -1.0 : 1.0;
        d.y = min(t, h2 - t);
    }
    else if (WrapY > 0.5)
    {
        d.y = ModP(d.y, ImgSize.y);
    }
    else
    {
        vis *= (d.y >= 0.0 && d.y <= ImgSize.y) ? 1.0 : 0.0;
    }

    return ImgMinTap.xy + d;
}

float2 WrapPixel(float2 p, out float vis)
{
    float2 s;
    return WrapPixelS(p, vis, s);
}

// ---------------------------------------------------------------------------
// シーン画素座標 q から Source を 1 タップ（ラップ / 枠外マスク込み）
// ---------------------------------------------------------------------------
float4 TapSource(float2 q, float2 P, float2 uv, float2 duvSx, float2 duvSy)
{
    float vis;
    float2 w = WrapPixel(q, vis);
    float2 d = w - P; // シーン単位
    float2 suv = uv + d.x * duvSx + d.y * duvSy;
    return SourceTexture.SampleLevel(SourceSampler, suv, 0) * vis;
}

// ---------------------------------------------------------------------------
// メイン
// ---------------------------------------------------------------------------
float4 main(
    float4 pos : SV_POSITION,
    float4 posScene : SCENE_POSITION,
    float4 uv0 : TEXCOORD0, // Source の座標
    float4 uv1 : TEXCOORD1, // Lens   の座標
    float4 uv2 : TEXCOORD2 // Matte  の座標
) : SV_Target
{
    float2 uv = uv0.xy;

    // シーン画素座標（タイル不変の絶対基準）
    float2 P = posScene.xy;

    // ------------------------------------------------------------------
    // 画面微分は、分岐（早期 return）より前にすべて計算しておく
    // ------------------------------------------------------------------
    float2 dPx = ddx(P);
    float2 dPy = ddy(P);
    float2 dUvx = ddx(uv);
    float2 dUvy = ddy(uv);
    float2 dLx = ddx(uv1.xy);
    float2 dLy = ddy(uv1.xy);
    float2 dMx = ddx(uv2.xy);
    float2 dMy = ddy(uv2.xy);

    // 画面 1 デバイスピクセルあたりのシーン単位の歩み
    float2 sPerPx = float2(dPx.x, dPy.y);
    sPerPx = float2(abs(sPerPx.x) > 1e-6 ? sPerPx.x : 1.0,
                    abs(sPerPx.y) > 1e-6 ? sPerPx.y : 1.0);

    // シーン 1 単位あたりの UV 変化
    float2 duvSx = dUvx / sPerPx.x;
    float2 duvSy = dUvy / sPerPx.y;
    float2 duvLx = dLx / sPerPx.x;
    float2 duvLy = dLy / sPerPx.y;
    float2 duvMx = dMx / sPerPx.x;
    float2 duvMy = dMy / sPerPx.y;

    // ------------------------------------------------------------------
    // 基準位置のラップ（クロップ外を折り返しで埋める）
    //   クロップ無しなら P は常に矩形内なので Pw == P（従来と同じ）。
    //   Pw を基準に Source / Lens / Matte を参照し、反射領域では
    //   勾配の向きを反転する。
    // ------------------------------------------------------------------
    float visP;
    float2 sgn;
    float2 Pw = WrapPixelS(P, visP, sgn);
    float2 dP = Pw - P;

    float2 uvC = uv + dP.x * duvSx + dP.y * duvSy;
    float2 luv = uv1.xy + dP.x * duvLx + dP.y * duvLy;
    float2 muv = uv2.xy + dP.x * duvMx + dP.y * duvMy;

    float4 center = SourceTexture.SampleLevel(SourceSampler, uvC, 0);
    bool alive = (visP > 0.5) && (center.a > 0.0005);

    // -----------------------------------------------------------------
    // レンズ（バンプマップ）から屈折方向を求める
    //   tap 幅の中心差分 = 幅 2*tap のローパス付き導関数。
    // -----------------------------------------------------------------
    float tap = max(ImgMinTap.z, 1.0);
    float2 ldx = dLx * (tap / sPerPx.x);
    float2 ldy = dLy * (tap / sPerPx.y);

    // 直交方向に半径 cr の箱型ステンシルで平均する（12タップ）
    float2 lcr = float2(dLx.x * (tap / sPerPx.x),
                        dLy.y * (tap / sPerPx.y));
    float gx = 0.0;
    float gy = 0.0;
    {
        float2 o1 = float2(0.0, -lcr.y);
        float2 o2 = float2(0.0, lcr.y);
        gx += Luminance(LensTexture.SampleLevel(LensSampler, luv + ldx + o1, 0).rgb)
            - Luminance(LensTexture.SampleLevel(LensSampler, luv - ldx + o1, 0).rgb);
        gx += Luminance(LensTexture.SampleLevel(LensSampler, luv + ldx, 0).rgb)
            - Luminance(LensTexture.SampleLevel(LensSampler, luv - ldx, 0).rgb);
        gx += Luminance(LensTexture.SampleLevel(LensSampler, luv + ldx + o2, 0).rgb)
            - Luminance(LensTexture.SampleLevel(LensSampler, luv - ldx + o2, 0).rgb);

        o1 = float2(-lcr.x, 0.0);
        o2 = float2(lcr.x, 0.0);
        gy += Luminance(LensTexture.SampleLevel(LensSampler, luv + ldy + o1, 0).rgb)
            - Luminance(LensTexture.SampleLevel(LensSampler, luv - ldy + o1, 0).rgb);
        gy += Luminance(LensTexture.SampleLevel(LensSampler, luv + ldy, 0).rgb)
            - Luminance(LensTexture.SampleLevel(LensSampler, luv - ldy, 0).rgb);
        gy += Luminance(LensTexture.SampleLevel(LensSampler, luv + ldy + o2, 0).rgb)
            - Luminance(LensTexture.SampleLevel(LensSampler, luv - ldy + o2, 0).rgb);
    }

    // 1 ピクセルあたりの輝度勾配 [luma/px]（3 ステンシル平均）
    float2 grad = float2(gx, gy) * (0.5 / (3.0 * tap));
    float gmag = length(grad);

    // 量子化ノイズの床を引き算（ソフトしきい値）
    float g = max(gmag - ImgMinTap.w, 0.0);
    float soft = g * max(Bump, 0.0);
    float slope = soft / (1.0 + soft);

    float2 warpDir = -(gmag > 1e-6 ? grad / gmag : float2(0.0, 0.0)) * slope;

    // 反射で折り返された軸は勾配の向きが反転する
    warpDir *= sgn;

    // -----------------------------------------------------------------
    // マット（歪み量のスケール）
    // -----------------------------------------------------------------
    float matte = 1.0;
    if (HasMatte > 0.5)
    {
        float4 m = MatteTexture.SampleLevel(MatteSampler, muv, 0);
        matte = saturate(MatteUse > 0.5 ? m.a : Luminance(m.rgb));
        if (HasMatte > 1.5)
            matte = 1.0 - matte;
    }

    // -----------------------------------------------------------------
    // 歪み方向の回転
    // -----------------------------------------------------------------
    float cs = cos(Rotate);
    float sn = sin(Rotate);
    float2 dispField = float2(
        warpDir.x * cs - warpDir.y * sn,
        warpDir.x * sn + warpDir.y * cs) * matte;

    // 適応フィルタ用：変位場の画面微分 [px/px]（分岐の前で計算）
    float2 dfdx = ddx(dispField);
    float2 dfdy = ddy(dispField);

    // ここで初めて早期 return（微分はすべて計算済み）
    if (!alive)
        return float4(0.0, 0.0, 0.0, 0.0);

    // -----------------------------------------------------------------
    // スペクトル合成
    // -----------------------------------------------------------------
    uint steps = (uint) clamp(Steps, 3.0, 100.0);
    bool filtered = UseFilter > 0.5;

    // 不透明度モード: 0=Normal, 1=All Opaque, 2=As Premult
    bool allOpaque = (Extra.y > 0.5 && Extra.y < 1.5);
    bool asPremult = (Extra.y > 1.5);

    float3 sumRGB = float3(0.0, 0.0, 0.0);  // Σ col * rgb(premult)
    float3 sumDen = float3(0.0, 0.0, 0.0);  // Σ col * a
    float3 sumW = float3(0.0, 0.0, 0.0);    // Σ col（WB ON なら 1、OFF なら Color1+2+3）
    float sumA = 0.0;
    float sumAW = 0.0;

    // 各色の三角形重みの総和 H1..H3（色を使わない軽い前計算）
    float3 Hs = float3(0.0, 0.0, 0.0);
    [loop]
    for (uint k = 0; k < steps; k++)
    {
        float tk = (float) k / (float) (steps - 1u);
        Hs += float3(max(1.0 - 2.0 * tk, 0.0),
                     1.0 - abs(2.0 * tk - 1.0),
                     max(2.0 * tk - 1.0, 0.0));
    }
    Hs = max(Hs, 1e-4);

    // ホワイトバランス: 3 色をチャンネルごとの合計で割り、合計を白にする
    float3 c1 = Color1.rgb;
    float3 c2 = Color2.rgb;
    float3 c3 = Color3.rgb;
    bool wb = Extra.x > 0.5;
    if (wb)
    {
        float3 csum = c1 + c2 + c3;
        float3 inv = float3(csum.x > 1e-4 ? 1.0 / csum.x : 0.0,
                            csum.y > 1e-4 ? 1.0 / csum.y : 0.0,
                            csum.z > 1e-4 ? 1.0 / csum.z : 0.0);
        c1 *= inv;
        c2 *= inv;
        c3 *= inv;
    }

    [loop]
    for (uint i = 0; i < steps; i++)
    {
        float t = (float) i / (float) (steps - 1u);

        // スペクトル色（三角形重みで補間。各色の総重みが等しくなるよう正規化）
        float3 hw = float3(max(1.0 - 2.0 * t, 0.0),
                           1.0 - abs(2.0 * t - 1.0),
                           max(2.0 * t - 1.0, 0.0)) / Hs;
        float3 col = c1 * hw.x + c2 * hw.y + c3 * hw.z;

        // スペクトル位置ごとの歪み量（赤端 -> 青端）
        float warp = lerp(WarpRed, WarpBlue, t);

        float wScalar = (col.r + col.g + col.b) * (1.0 / 3.0);

        // このステップでの変位量（ピクセル、XY別）
        float2 pixelScale = Amount * warp;
        float2 D = dispField * pixelScale; // [device px]
        float2 baseP = P + D * sPerPx;     // シーン画素座標

        // シーン空間でラップ（タイル不変）
        float visB;
        float2 wB = WrapPixel(baseP, visB);

        float4 s;
        if (filtered)
        {
            // 縮小される領域（|dD/dp| が大きい）だけボックスフィルタする。
            float2 Fx = dfdx * pixelScale; // [px/px]
            float2 Fy = dfdy * pixelScale;
            float fp = min(max(length(Fx), length(Fy)), 4.0);
            float o = fp * 0.25;

            float2 Ax = float2(1.0, 0.0) + Fx;
            float2 Ay = float2(0.0, 1.0) + Fy;

            float2 os = o * sPerPx;
            float4 a = TapSource(wB + (Ax + Ay) * os, P, uv, duvSx, duvSy);
            float4 b = TapSource(wB + (Ax - Ay) * os, P, uv, duvSx, duvSy);
            float4 c = TapSource(wB - (Ax - Ay) * os, P, uv, duvSx, duvSy);
            float4 d = TapSource(wB - (Ax + Ay) * os, P, uv, duvSx, duvSy);

            s = (a + b + c + d) * 0.25;
        }
        else
        {
            s = TapSource(wB, P, uv, duvSx, duvSy);
        }

        // All Opaque: 入力を不透明として扱う（枠外/完全透明だけ 0）
        if (allOpaque)
            s.a = (s.a > 0.0005) ? 1.0 : 0.0;

        sumRGB += col * s.rgb;
        sumDen += col * s.a;
        sumA += wScalar * s.a;

        sumW += col;
        sumAW += wScalar;
    }

    float outA = saturate(sumAW > 1e-5 ? sumA / sumAW : center.a);

    float3 outRGB;
    if (asPremult)
    {
        // As Premult: プリマルチ済みの RGB を各チャンネル独立に合成
        outRGB = sumRGB;
    }
    else
    {
        // Normal / All Opaque: アルファ加重でストレート色を求め、
        // 色の総量（sumW）を掛けて、合成後のアルファで再プリマルチする
        float3 straightRGB = sumRGB / max(sumDen, 1e-4);
        outRGB = straightRGB * sumW * outA;
    }

    // WB OFF で色の合計が 1 を超える場合の飽和（プリマルチの rgb <= a を保つ）
    if (!wb)
        outRGB = min(outRGB, float3(outA, outA, outA));

    return float4(outRGB, outA);
}
