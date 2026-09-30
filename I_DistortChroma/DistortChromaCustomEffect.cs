using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace I_DistortChroma
{
    /// <summary>
    /// DistortChromaShader.hlsl を D2D1 のカスタムシェーダーエフェクトとして扱うためのラッパー。
    /// 入力は 3 つ（Source / Lens / Matte）。
    /// </summary>
    internal sealed class DistortChromaCustomEffect : D2D1CustomShaderEffectBase
    {
        public float AmountX { set => SetValue((int)Props.AmountX, value); }
        public float AmountY { set => SetValue((int)Props.AmountY, value); }
        public float WarpRed { set => SetValue((int)Props.WarpRed, value); }
        public float WarpBlue { set => SetValue((int)Props.WarpBlue, value); }
        public float Steps { set => SetValue((int)Props.Steps, value); }
        public float Rotate { set => SetValue((int)Props.Rotate, value); }
        public float HasMatte { set => SetValue((int)Props.HasMatte, value); }
        public float MatteUse { set => SetValue((int)Props.MatteUse, value); }
        public Vector4 Color1 { set => SetValue((int)Props.Color1, value); }
        public Vector4 Color2 { set => SetValue((int)Props.Color2, value); }
        public Vector4 Color3 { set => SetValue((int)Props.Color3, value); }
        public float UseFilter { set => SetValue((int)Props.UseFilter, value); }
        public float WrapX { set => SetValue((int)Props.WrapX, value); }
        public float WrapY { set => SetValue((int)Props.WrapY, value); }
        public float Bump { set => SetValue((int)Props.Bump, value); }

        /// <summary>xy=処理矩形の左上シーン座標（クロップ適用後）, z=勾配タップ幅[px], w=勾配ノイズフロア</summary>
        public Vector4 ImgMinTap { set => SetValue((int)Props.ImgMinTap, value); }

        /// <summary>xy=処理矩形のサイズ[px]（クロップ適用後。0以下=境界不明）</summary>
        public Vector4 ImgSize { set => SetValue((int)Props.ImgSize, value); }

        /// <summary>x=ホワイトバランス(0/1), y=不透明度(0=Normal,1=AllOpaque,2=AsPremult), z=クロップ有効(0/1)</summary>
        public Vector4 Extra { set => SetValue((int)Props.Extra, value); }

        public DistortChromaCustomEffect(IGraphicsDevicesAndContext devices)
            : base(Create<EffectImpl>(devices))
        {
        }

        private enum Props
        {
            AmountX,
            AmountY,
            WarpRed,
            WarpBlue,
            Steps,
            Rotate,
            HasMatte,
            MatteUse,
            Color1,
            Color2,
            Color3,
            UseFilter,
            WrapX,
            WrapY,
            Bump,
            ImgMinTap,
            ImgSize,
            Extra,
        }

        /// <summary>
        /// 定数バッファ。HLSL 側の cbuffer Constants と完全に一致させること。
        /// （HLSL の float4 は 16 バイト整列。C# 側は preceding size が 16 の倍数の
        ///  位置に置くことで一致させている。安易にフィールドを挿入しないこと。）
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public Vector2 Amount;   // AmountX, AmountY
            public float WarpRed;
            public float WarpBlue;

            public float Steps;
            public float Rotate;
            public float HasMatte;
            public float MatteUse;

            public Vector4 Color1;
            public Vector4 Color2;
            public Vector4 Color3;

            public float UseFilter;
            public float WrapX;
            public float WrapY;
            public float Bump;

            public Vector4 ImgMinTap;  // xy=処理矩形左上, z=勾配タップ, w=ノイズフロア
            public Vector4 ImgSize;    // xy=処理矩形サイズ[px]
            public Vector4 Extra;      // x=WB, y=不透明度モード, z=クロップ有効
        }

        [CustomEffect(3)]
        private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
        {
            private ConstantBuffer constants;

            public EffectImpl()
                : base(LoadShader())
            {
                constants = new ConstantBuffer
                {
                    Amount = new Vector2(10f, 10f),
                    WarpRed = 0.5f,
                    WarpBlue = 1f,
                    Steps = 8f,
                    Rotate = 0f,
                    HasMatte = 0f,
                    MatteUse = 0f,
                    Color1 = new Vector4(1f, 0f, 0f, 1f),
                    Color2 = new Vector4(0f, 1f, 0f, 1f),
                    Color3 = new Vector4(0f, 0f, 1f, 1f),
                    UseFilter = 1f,
                    WrapX = 2f,
                    WrapY = 2f,
                    Bump = 4f,
                    ImgMinTap = new Vector4(0f, 0f, 1f, 0.75f / 255f),
                    ImgSize = new Vector4(0f, 0f, 0f, 0f),
                    Extra = new Vector4(0f, 0f, 0f, 0f),
                };
            }

            private static byte[] LoadShader()
            {
                var assembly = Assembly.GetExecutingAssembly();
                string? name = Array.Find(assembly.GetManifestResourceNames(),
                    x => x.EndsWith("DistortChromaShader.cso", StringComparison.OrdinalIgnoreCase));
                if (name is null)
                    throw new FileNotFoundException("DistortChromaShader.cso が埋め込まれていません。");

                using var stream = assembly.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }

            protected override void UpdateConstants()
            {
                drawInformation?.SetPixelShaderConstantBuffer(in constants);
            }

            // ------------------------------------------------------------------
            // 入出力矩形
            // ------------------------------------------------------------------
            public override void MapInputRectsToOutputRect(
                RawRect[] inputRects,
                RawRect[] inputOpaqueSubRects,
                out RawRect outputRect,
                out RawRect outputOpaqueSubRect)
            {
                outputRect = inputRects.Length > 0 ? inputRects[0] : new RawRect();
                outputOpaqueSubRect = new RawRect();
            }

            private static RawRect Union(RawRect a, RawRect b)
                => new RawRect(
                    Math.Min(a.Left, b.Left),
                    Math.Min(a.Top, b.Top),
                    Math.Max(a.Right, b.Right),
                    Math.Max(a.Bottom, b.Bottom));

            public override void MapOutputRectToInputRects(RawRect outputRect, RawRect[] inputRects)
            {
                // 歪み量（Amount × Warp）+ 適応フィルタのボックス(最大約10px)
                // + 勾配タップ幅 tap 分のマージン（のりしろ）を要求する。
                float maxAmount = Math.Max(Math.Abs(constants.Amount.X), Math.Abs(constants.Amount.Y));
                float maxWarp = Math.Max(Math.Abs(constants.WarpRed), Math.Abs(constants.WarpBlue));
                float tap = Math.Max(1f, constants.ImgMinTap.Z);
                int margin = (int)Math.Ceiling(maxAmount * maxWarp) + (int)Math.Ceiling(tap) + 16;

                var expanded = new RawRect(
                    outputRect.Left - margin,
                    outputRect.Top - margin,
                    outputRect.Right + margin,
                    outputRect.Bottom + margin);

                // v3: クロップ + ラップ有効時は、クロップ外の画素がクロップ矩形内の
                // 離れた位置を参照する。出力タイルが分割されても参照先が欠けない
                // ように、クロップ矩形全体（+マージン）も要求に含める。
                bool needCrop =
                    constants.Extra.Z > 0.5f
                    && constants.ImgSize.X > 0f && constants.ImgSize.Y > 0f
                    && (constants.WrapX > 0.5f || constants.WrapY > 0.5f);

                RawRect cropRect = default;
                if (needCrop)
                {
                    cropRect = new RawRect(
                        (int)Math.Floor(constants.ImgMinTap.X) - margin,
                        (int)Math.Floor(constants.ImgMinTap.Y) - margin,
                        (int)Math.Ceiling(constants.ImgMinTap.X + constants.ImgSize.X) + margin,
                        (int)Math.Ceiling(constants.ImgMinTap.Y + constants.ImgSize.Y) + margin);
                    expanded = Union(expanded, cropRect);
                }

                // 入力0 (Source): 歪み量 + フィルタ分のマージンを要求
                if (inputRects.Length > 0)
                    inputRects[0] = expanded;

                // 入力1 (Lens): 勾配タップ分を含むマージンを要求
                if (inputRects.Length > 1)
                    inputRects[1] = expanded;

                // 入力2 (Matte): ピクセル位置そのまま（オフセット無し）でしか
                // サンプリングしないので出力矩形だけで十分（クロップ時は基準位置が
                // クロップ矩形内へ移るので、その分だけ含める）。
                // マット無効時は 1x1 の最小矩形を要求してタイリングを回避する。
                if (inputRects.Length > 2)
                {
                    if (constants.HasMatte > 0.5f)
                        inputRects[2] = needCrop ? Union(outputRect, cropRect) : outputRect;
                    else
                        inputRects[2] = new RawRect(0, 0, 1, 1);
                }
            }

            // ------------------------------------------------------------------
            // プロパティ
            // ------------------------------------------------------------------
            [CustomEffectProperty(PropertyType.Float, 0)]
            public float AmountX
            {
                get => constants.Amount.X;
                set { constants.Amount.X = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 1)]
            public float AmountY
            {
                get => constants.Amount.Y;
                set { constants.Amount.Y = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 2)]
            public float WarpRed
            {
                get => constants.WarpRed;
                set { constants.WarpRed = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 3)]
            public float WarpBlue
            {
                get => constants.WarpBlue;
                set { constants.WarpBlue = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 4)]
            public float Steps
            {
                get => constants.Steps;
                set { constants.Steps = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 5)]
            public float Rotate
            {
                get => constants.Rotate;
                set { constants.Rotate = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 6)]
            public float HasMatte
            {
                get => constants.HasMatte;
                set { constants.HasMatte = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 7)]
            public float MatteUse
            {
                get => constants.MatteUse;
                set { constants.MatteUse = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Vector4, 8)]
            public Vector4 Color1
            {
                get => constants.Color1;
                set { constants.Color1 = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Vector4, 9)]
            public Vector4 Color2
            {
                get => constants.Color2;
                set { constants.Color2 = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Vector4, 10)]
            public Vector4 Color3
            {
                get => constants.Color3;
                set { constants.Color3 = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 11)]
            public float UseFilter
            {
                get => constants.UseFilter;
                set { constants.UseFilter = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 12)]
            public float WrapX
            {
                get => constants.WrapX;
                set { constants.WrapX = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 13)]
            public float WrapY
            {
                get => constants.WrapY;
                set { constants.WrapY = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Float, 14)]
            public float Bump
            {
                get => constants.Bump;
                set { constants.Bump = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Vector4, 15)]
            public Vector4 ImgMinTap
            {
                get => constants.ImgMinTap;
                set { constants.ImgMinTap = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Vector4, 16)]
            public Vector4 ImgSize
            {
                get => constants.ImgSize;
                set { constants.ImgSize = value; UpdateConstants(); }
            }

            [CustomEffectProperty(PropertyType.Vector4, 17)]
            public Vector4 Extra
            {
                get => constants.Extra;
                set { constants.Extra = value; UpdateConstants(); }
            }
        }
    }
}
