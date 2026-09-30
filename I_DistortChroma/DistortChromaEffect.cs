using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using YukkuriMovieMaker.Brush;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Brush;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;
using Brush = YukkuriMovieMaker.Plugin.Brush.Brush;

namespace I_DistortChroma
{
    /// <summary>レンズ（歪み方向を決めるバンプマップ）の取得元</summary>
    public enum DistortChromaLensSource
    {
        [Display(Name = "アイテム自身")]
        Self,

        [Display(Name = "別の画像・シーン")]
        Other,
    }

    /// <summary>マット（歪み量のスケール）の取得元</summary>
    public enum DistortChromaMatteSource
    {
        [Display(Name = "なし")]
        None,

        [Display(Name = "別の画像・シーン")]
        Other,
    }

    /// <summary>枠外の参照方法</summary>
    public enum DistortChromaWrap
    {
        [Display(Name = "なし（黒）")]
        None,

        [Display(Name = "タイル")]
        Tile,

        [Display(Name = "反射")]
        Reflect,
    }

    /// <summary>マットとして使うチャンネル</summary>
    public enum DistortChromaMatteUse
    {
        [Display(Name = "輝度")]
        Luma,

        [Display(Name = "アルファ")]
        Alpha,
    }

    /// <summary>不透明度（アルファ）の扱い。数値はシェーダーの Extra.y と一致させること。</summary>
    public enum DistortChromaOpacity
    {
        [Display(Name = "通常", Description = "アルファを考慮して色を合成します。")]
        Normal = 0,

        [Display(Name = "すべて不透明", Description = "入力を不透明として扱います。不透明な素材向け。")]
        AllOpaque = 1,

        [Display(Name = "プリマルチとして処理", Description = "各チャンネルを独立に処理します。従来の挙動です。")]
        AsPremult = 2,
    }

    [VideoEffect("I_DistortChroma", ["加工"], ["distort", "chroma", "色収差", "色ズレ", "屈折", "プリズム", "歪み"], true, true, IsAviUtlSupported = false)]
    public sealed class DistortChromaEffect : VideoEffectBase, IFileItem, IResourceItem
    {
        public override string Label => "I_DistortChroma";

        // ==================================================================
        //  基本
        // ==================================================================
        [Display(GroupName = "基本", Name = "歪み量", Description = "レンズ歪みの強さ（ピクセル）です。負の値で歪み方向が反転します。", Order = 100)]
        [AnimationSlider("F2", "px", -10000, 10000)]
        public Animation Amount { get; } = new Animation(3000.0, -100000.0, 100000.0);

        [Display(GroupName = "基本", Name = "横方向の相対量", Description = "横方向の歪み量の倍率です。", Order = 100)]
        [AnimationSlider("F2", "x", 0.0, 4.0)]
        public Animation AmountRelX { get; } = new Animation(1.0, 0.0, 100.0);

        [Display(GroupName = "基本", Name = "縦方向の相対量", Description = "縦方向の歪み量の倍率です。", Order = 100)]
        [AnimationSlider("F2", "x", 0.0, 4.0)]
        public Animation AmountRelY { get; } = new Animation(1.0, 0.0, 100.0);

        [Display(GroupName = "基本", Name = "歪み方向の回転", Description = "歪む向きを回転させます。0以外でねじれたような歪みになります。", Order = 200)]
        [AnimationSlider("F1", "°", -180, 180)]
        public Animation RotateWarpDir { get; } = new Animation(0.0, -100000.0, 100000.0);

        [Display(GroupName = "基本", Name = "勾配感度", Description = "レンズの明暗差に対する感度です。大きいほど僅かな明暗でも歪みます。", Order = 200)]
        [AnimationSlider("F2", "", 0.0, 20.0)]
        public Animation BumpStrength { get; } = new Animation(4.0, 0.0, 1000.0);

        [Display(GroupName = "基本", Name = "不透明度の扱い", Description = "アルファチャンネルの処理方法です。半透明の素材では「通常」を使ってください。", Order = 300)]
        [EnumComboBox]
        public DistortChromaOpacity Opacity
        {
            get => opacity;
            set => Set(ref opacity, value);
        }
        private DistortChromaOpacity opacity = DistortChromaOpacity.Normal;

        // ==================================================================
        //  スペクトラム
        // ==================================================================
        [Display(GroupName = "スペクトラム", Name = "ステップ数", Description = "スペクトルの分割数です。多いほど滑らかになりますが処理が重くなります。", Order = 100)]
        [AnimationSlider("F0", "段", 3, 32)]
        public Animation Steps { get; } = new Animation(8.0, 3.0, 100.0);

        [Display(GroupName = "スペクトラム", Name = "赤端の歪み量", Description = "スペクトル赤端の歪みの倍率です。負の値で方向が反転します。", Order = 100)]
        [AnimationSlider("F2", "", -2.0, 2.0)]
        public Animation WarpRed { get; } = new Animation(0.5, -1000.0, 1000.0);

        [Display(GroupName = "スペクトラム", Name = "青端の歪み量", Description = "スペクトル青端の歪みの倍率です。負の値で方向が反転します。", Order = 100)]
        [AnimationSlider("F2", "", -2.0, 2.0)]
        public Animation WarpBlue { get; } = new Animation(1.0, -1000.0, 1000.0);

        [Display(GroupName = "スペクトラム", Name = "色1（赤端）", Description = "スペクトルの赤側の色です。", Order = 200)]
        [ColorPicker]
        public Color Color1
        {
            get => color1;
            set => Set(ref color1, value);
        }
        private Color color1 = Colors.Red;

        [Display(GroupName = "スペクトラム", Name = "色2（中間）", Description = "スペクトルの中央の色です。", Order = 200)]
        [ColorPicker]
        public Color Color2
        {
            get => color2;
            set => Set(ref color2, value);
        }
        private Color color2 = Colors.Lime;

        [Display(GroupName = "スペクトラム", Name = "色3（青端）", Description = "スペクトルの青側の色です。", Order = 200)]
        [ColorPicker]
        public Color Color3
        {
            get => color3;
            set => Set(ref color3, value);
        }
        private Color color3 = Colors.Blue;

        [Display(GroupName = "スペクトラム", Name = "ホワイトバランス", Description = "3色が白に合成されるように内部調整します。歪んでいない部分の色が変化しにくくなります。", Order = 300)]
        [ToggleSlider]
        public bool WhiteBalance
        {
            get => whiteBalance;
            set => Set(ref whiteBalance, value);
        }
        private bool whiteBalance;

        // ==================================================================
        //  レンズ
        // ==================================================================
        [Display(GroupName = "レンズ", Name = "レンズソース", Description = "歪み方向を計算するバンプマップの取得元です。", Order = 100)]
        [EnumComboBox]
        public DistortChromaLensSource LensSource
        {
            get => lensSource;
            set => Set(ref lensSource, value);
        }
        private DistortChromaLensSource lensSource = DistortChromaLensSource.Self;

        [Display(GroupName = "レンズ", Name = "レンズ画像", Description = "「別の画像・シーン」選択時に使用する画像です。", Order = 200, AutoGenerateField = true)]
        public Brush LensBrush { get; } = CreateImageBrush();

        [Display(GroupName = "レンズ", Name = "レンズのぼかし", Description = "レンズ画像をぼかしてから使用します。エッジのギザつきを抑えます。", Order = 100)]
        [AnimationSlider("F2", "px", 0.0, 40.0)]
        public Animation BlurLens { get; } = new Animation(20.0, 0.0, 250.0);

        // ==================================================================
        //  マット
        // ==================================================================
        [Display(GroupName = "マット", Name = "マットソース", Description = "歪み量をスケールする画像です。黒い部分は歪まなくなります。", Order = 100)]
        [EnumComboBox]
        public DistortChromaMatteSource MatteSource
        {
            get => matteSource;
            set => Set(ref matteSource, value);
        }
        private DistortChromaMatteSource matteSource = DistortChromaMatteSource.None;

        [Display(GroupName = "マット", Name = "マット画像", Description = "マットとして使用する画像です。", Order = 200, AutoGenerateField = true)]
        public Brush MatteBrush { get; } = CreateImageBrush();

        [Display(GroupName = "マット", Name = "マットのぼかし", Description = "マットをぼかしてから使用します。境界を滑らかにします。", Order = 100)]
        [AnimationSlider("F2", "px", 0.0, 20.0)]
        public Animation BlurMatte { get; } = new Animation(0.0, 0.0, 250.0);

        [Display(GroupName = "マット", Name = "マット反転", Description = "マットの白黒を反転します。", Order = 100)]
        [ToggleSlider]
        public bool InvertMatte
        {
            get => invertMatte;
            set => Set(ref invertMatte, value);
        }
        private bool invertMatte;

        [Display(GroupName = "マット", Name = "マットの使い方", Description = "マット画像のどちらを使ってマスクを作るかです。", Order = 100)]
        [EnumComboBox]
        public DistortChromaMatteUse MatteUse
        {
            get => matteUse;
            set => Set(ref matteUse, value);
        }
        private DistortChromaMatteUse matteUse = DistortChromaMatteUse.Luma;

        // ==================================================================
        //  枠外の扱い
        // ==================================================================
        [Display(GroupName = "枠外", Name = "横方向のラップ", Description = "画像の外側を参照したときの処理です。", Order = 100)]
        [EnumComboBox]
        public DistortChromaWrap WrapX
        {
            get => wrapX;
            set => Set(ref wrapX, value);
        }
        private DistortChromaWrap wrapX = DistortChromaWrap.Reflect;

        [Display(GroupName = "枠外", Name = "縦方向のラップ", Description = "画像の外側を参照したときの処理です。", Order = 100)]
        [EnumComboBox]
        public DistortChromaWrap WrapY
        {
            get => wrapY;
            set => Set(ref wrapY, value);
        }
        private DistortChromaWrap wrapY = DistortChromaWrap.Reflect;

        [Display(GroupName = "枠外", Name = "適応フィルタ", Description = "縮小される領域の再サンプリングを滑らかにします。", Order = 200)]
        [ToggleSlider]
        public bool UseFilter
        {
            get => useFilter;
            set => Set(ref useFilter, value);
        }
        private bool useFilter = true;

        // ==================================================================
        //  クロップ
        // ==================================================================
        [Display(GroupName = "クロップ", Name = "上", Description = "上端から処理対象外にする幅です。クロップ外はラップ設定に従って埋められます（「なし」なら透明）。", Order = 100)]
        [AnimationSlider("F1", "px", 0, 2000)]
        public Animation CropTop { get; } = new Animation(0.0, 0.0, 100000.0);

        [Display(GroupName = "クロップ", Name = "下", Description = "下端から処理対象外にする幅です。", Order = 100)]
        [AnimationSlider("F1", "px", 0, 2000)]
        public Animation CropBottom { get; } = new Animation(0.0, 0.0, 100000.0);

        [Display(GroupName = "クロップ", Name = "左", Description = "左端から処理対象外にする幅です。", Order = 100)]
        [AnimationSlider("F1", "px", 0, 2000)]
        public Animation CropLeft { get; } = new Animation(0.0, 0.0, 100000.0);

        [Display(GroupName = "クロップ", Name = "右", Description = "右端から処理対象外にする幅です。", Order = 100)]
        [AnimationSlider("F1", "px", 0, 2000)]
        public Animation CropRight { get; } = new Animation(0.0, 0.0, 100000.0);

        // ==================================================================

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        {
            return new DistortChromaEffectProcessor(devices, this);
        }

        protected override IEnumerable<IAnimatable> GetAnimatables()
            => [Amount, AmountRelX, AmountRelY, RotateWarpDir, BumpStrength,
                Steps, WarpRed, WarpBlue, BlurLens, BlurMatte, LensBrush, MatteBrush,
                CropTop, CropBottom, CropLeft, CropRight];

        public override IEnumerable<string> GetFiles()
        {
            foreach (var file in base.GetFiles()) yield return file;
            foreach (var file in LensBrush.GetFiles()) yield return file;
            foreach (var file in MatteBrush.GetFiles()) yield return file;
        }

        public override void ReplaceFile(string from, string to)
        {
            base.ReplaceFile(from, to);
            LensBrush.ReplaceFile(from, to);
            MatteBrush.ReplaceFile(from, to);
        }

        public override IEnumerable<TimelineResource> GetResources()
        {
            foreach (var res in base.GetResources()) yield return res;
            foreach (var res in LensBrush.GetResources()) yield return res;
            foreach (var res in MatteBrush.GetResources()) yield return res;
        }

        /// <summary>
        /// 画像を読み込めるブラシ（BitmapBrush）を既定値として生成する。
        /// 見つからない場合は既定のブラシにフォールバックする。
        /// </summary>
        private static Brush CreateImageBrush()
        {
            try
            {
                var type = PluginLoader.Plugins
                    .OfType<IBrushPlugin>()
                    .FirstOrDefault(x => x.GetType().Name == "BitmapBrushPlugin")
                    ?.GetType();
                if (type is null) return new Brush();

                var create = typeof(Brush).GetMethods()
                    .First(m => m.Name == "Create" && m.IsGenericMethod);
                return (Brush)create.MakeGenericMethod(type).Invoke(null, null)!;
            }
            catch
            {
                return new Brush();
            }
        }
    }
}
