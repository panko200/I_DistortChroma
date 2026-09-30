using System;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Brush;

namespace I_DistortChroma
{
    internal sealed class DistortChromaEffectProcessor : IVideoEffectProcessor
    {
        private readonly IGraphicsDevicesAndContext devices;
        private readonly DistortChromaEffect item;
        private readonly DisposeCollector disposer = new();

        private ID2D1Image? input;
        private DistortChromaCustomEffect? effect;
        private GaussianBlur? lensBlur;
        private GaussianBlur? matteBlur;
        private IBrushSource? lensBrushSource;
        private IBrushSource? matteBrushSource;
        private ID2D1CommandList? lensCommandList;
        private ID2D1CommandList? matteCommandList;
        private ID2D1Bitmap? emptyBitmap;
        private ID2D1Image? output;

        public ID2D1Image Output => output ?? input ?? emptyBitmap!;

        public DistortChromaEffectProcessor(IGraphicsDevicesAndContext devices, DistortChromaEffect item)
        {
            this.devices = devices;
            this.item = item;

            var dc = devices.DeviceContext;

            emptyBitmap = dc.CreateEmptyBitmap();
            disposer.Collect(emptyBitmap);

            try
            {
                effect = new DistortChromaCustomEffect(devices);
                if (!effect.IsEnabled)
                {
                    effect.Dispose();
                    effect = null;
                }
            }
            catch
            {
                effect = null;
            }

            // シェーダーの生成に失敗した場合は素通しする
            if (effect is null)
                return;

            effect.InputCount = 3;
            disposer.Collect(effect);

            lensBlur = new GaussianBlur(dc) { Optimization = GaussianBlurOptimization.Quality };
            lensBlur.SetInput(0, emptyBitmap, true);
            disposer.Collect(lensBlur);

            matteBlur = new GaussianBlur(dc) { Optimization = GaussianBlurOptimization.Quality };
            matteBlur.SetInput(0, emptyBitmap, true);
            disposer.Collect(matteBlur);

            output = effect.Output;
            disposer.Collect(output);
        }

        public void SetInput(ID2D1Image? input)
        {
            this.input = input;
            effect?.SetInput(0, input, true);
        }

        public void ClearInput()
        {
            effect?.SetInput(0, null, true);
            effect?.SetInput(1, null, true);
            effect?.SetInput(2, null, true);
            lensBlur?.SetInput(0, null, true);
            matteBlur?.SetInput(0, null, true);
            input = null;
        }

        public DrawDescription Update(EffectDescription effectDescription)
        {
            if (effect is null || input is null)
                return effectDescription.DrawDescription;

            var dc = devices.DeviceContext;
            int frame = effectDescription.ItemPosition.Frame;
            int length = effectDescription.ItemDuration.Frame;
            int fps = effectDescription.FPS;

            float amount = (float)item.Amount.GetValue(frame, length, fps);
            float relX = (float)item.AmountRelX.GetValue(frame, length, fps);
            float relY = (float)item.AmountRelY.GetValue(frame, length, fps);
            float rotate = (float)item.RotateWarpDir.GetValue(frame, length, fps);
            float bump = (float)item.BumpStrength.GetValue(frame, length, fps);
            float steps = (float)item.Steps.GetValue(frame, length, fps);
            float warpRed = (float)item.WarpRed.GetValue(frame, length, fps);
            float warpBlue = (float)item.WarpBlue.GetValue(frame, length, fps);
            float blurLens = (float)item.BlurLens.GetValue(frame, length, fps);
            float blurMatte = (float)item.BlurMatte.GetValue(frame, length, fps);
            float cropTop = (float)item.CropTop.GetValue(frame, length, fps);
            float cropBottom = (float)item.CropBottom.GetValue(frame, length, fps);
            float cropLeft = (float)item.CropLeft.GetValue(frame, length, fps);
            float cropRight = (float)item.CropRight.GetValue(frame, length, fps);

            var bounds = dc.GetImageLocalBounds(input);
            bool hasBounds = IsValidBounds(bounds);

            // ---------------------------------------------------------------
            // t1 : レンズ（バンプマップ）
            // ---------------------------------------------------------------
            ID2D1Image lensImage = input;
            if (item.LensSource == DistortChromaLensSource.Other && hasBounds)
            {
                lensBrushSource ??= CreateBrushSource(item.LensBrush);
                lensBrushSource.Update(new BrushSourceDescription(effectDescription, bounds));

                var list = DrawBrushLayer(dc, lensBrushSource.Brush, bounds);
                disposer.RemoveAndDispose(ref lensCommandList);
                lensCommandList = list;
                disposer.Collect(lensCommandList);
                lensImage = lensCommandList;
            }

            if (blurLens > 0.01f)
            {
                lensBlur!.StandardDeviation = blurLens;
                lensBlur.SetInput(0, lensImage, true);
                using var blurred = lensBlur.Output;
                effect.SetInput(1, blurred, true);
            }
            else
            {
                lensBlur!.SetInput(0, emptyBitmap, true);
                effect.SetInput(1, lensImage, true);
            }

            // ---------------------------------------------------------------
            // t2 : マット
            // ---------------------------------------------------------------
            float hasMatte = 0f;
            ID2D1Image matteImage = emptyBitmap!;
            if (item.MatteSource == DistortChromaMatteSource.Other && hasBounds)
            {
                matteBrushSource ??= CreateBrushSource(item.MatteBrush);
                matteBrushSource.Update(new BrushSourceDescription(effectDescription, bounds));

                var list = DrawBrushLayer(dc, matteBrushSource.Brush, bounds);
                disposer.RemoveAndDispose(ref matteCommandList);
                matteCommandList = list;
                disposer.Collect(matteCommandList);
                matteImage = matteCommandList;

                if (blurMatte > 0.01f)
                {
                    matteBlur!.StandardDeviation = blurMatte;
                    matteBlur.SetInput(0, matteImage, true);
                    using var blurred = matteBlur.Output;
                    effect.SetInput(2, blurred, true);
                }
                else
                {
                    matteBlur!.SetInput(0, emptyBitmap, true);
                    effect.SetInput(2, matteImage, true);
                }

                hasMatte = item.InvertMatte ? 2f : 1f;
            }
            else
            {
                matteBlur!.SetInput(0, emptyBitmap, true);
                effect.SetInput(2, emptyBitmap, true);
            }

            // ---------------------------------------------------------------
            // 定数
            // ---------------------------------------------------------------
            //   tap   = max(1, レンズぼかしσ) … 差分のローパス幅（帯域制限微分）
            //   floor = 0.75 / (255 * tap)   … 8bit 量子化ノイズの理論床
            float gradTap = Math.Max(1f, blurLens);
            float gradFloor = 0.75f / (255f * gradTap);

            float cropActive = 0f;
            if (hasBounds)
            {
                // ----- クロップ -----
                // 画像境界（シーン座標）から各辺を削った矩形を「処理矩形」として渡す。
                // 処理矩形が 1px 未満にならないよう、辺ごとにクランプする。
                float w = bounds.Right - bounds.Left;
                float h = bounds.Bottom - bounds.Top;

                float cl = Math.Clamp(float.IsFinite(cropLeft) ? cropLeft : 0f, 0f, Math.Max(0f, w - 1f));
                float cr = Math.Clamp(float.IsFinite(cropRight) ? cropRight : 0f, 0f, Math.Max(0f, w - 1f - cl));
                float ct = Math.Clamp(float.IsFinite(cropTop) ? cropTop : 0f, 0f, Math.Max(0f, h - 1f));
                float cb = Math.Clamp(float.IsFinite(cropBottom) ? cropBottom : 0f, 0f, Math.Max(0f, h - 1f - ct));

                if (cl + cr + ct + cb > 0.001f)
                    cropActive = 1f;

                effect.ImgMinTap = new Vector4(bounds.Left + cl, bounds.Top + ct, gradTap, gradFloor);
                effect.ImgSize = new Vector4(w - cl - cr, h - ct - cb, 0f, 0f);
            }
            else
            {
                // 境界が取得できない場合はサイズ 0 を渡してラップ/マスクを無効化
                effect.ImgMinTap = new Vector4(0f, 0f, gradTap, gradFloor);
                effect.ImgSize = new Vector4(0f, 0f, 0f, 0f);
            }

            // x=ホワイトバランス, y=不透明度モード, z=クロップ有効
            effect.Extra = new Vector4(
                item.WhiteBalance ? 1f : 0f,
                (float)(int)item.Opacity,
                cropActive,
                0f);

            // ホワイトバランスの色補正はシェーダー側（Extra.x と正規化方式）へ移した。
            // 旧実装の「色をチャンネル総和で割る」処理は、シェーダーの
            // sumRGB / sumW で打ち消されて無効だったため削除。
            effect.Color1 = ToVector4(item.Color1);
            effect.Color2 = ToVector4(item.Color2);
            effect.Color3 = ToVector4(item.Color3);

            effect.AmountX = amount * relX;
            effect.AmountY = amount * relY;
            effect.WarpRed = warpRed;
            effect.WarpBlue = warpBlue;
            effect.Steps = steps;
            effect.Rotate = (float)((rotate % 360.0) * Math.PI / 180.0);
            effect.HasMatte = hasMatte;
            effect.MatteUse = item.MatteUse == DistortChromaMatteUse.Alpha ? 1f : 0f;
            effect.UseFilter = item.UseFilter ? 1f : 0f;
            effect.WrapX = (float)(int)item.WrapX;
            effect.WrapY = (float)(int)item.WrapY;
            effect.Bump = bump;

            return effectDescription.DrawDescription;
        }

        public void Dispose()
        {
            ClearInput();
            disposer.DisposeAndClear();
        }

        private IBrushSource CreateBrushSource(Brush brush)
        {
            var source = brush.CreateBrush(devices);
            disposer.Collect(source);
            return source;
        }

        /// <summary>
        /// ブラシを入力画像と同じ範囲に描き込み、コマンドリストとして返す。
        /// </summary>
        private static ID2D1CommandList DrawBrushLayer(ID2D1DeviceContext6 dc, ID2D1Brush brush, RawRectF bounds)
        {
            var list = dc.CreateCommandList();
            dc.Target = list;
            dc.BeginDraw();
            dc.Clear(null);
            dc.FillRectangle(bounds, brush);
            dc.EndDraw();
            dc.Target = null;
            list.Close();
            return list;
        }

        private static bool IsValidBounds(RawRectF b)
            => float.IsFinite(b.Left) && float.IsFinite(b.Top)
            && float.IsFinite(b.Right) && float.IsFinite(b.Bottom)
            && b.Right > b.Left && b.Bottom > b.Top
            && Math.Abs(b.Left) < 2e9f && Math.Abs(b.Top) < 2e9f
            && Math.Abs(b.Right) < 2e9f && Math.Abs(b.Bottom) < 2e9f;

        private static Vector4 ToVector4(System.Windows.Media.Color c)
            => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    }
}
