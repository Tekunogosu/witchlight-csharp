using System;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Mapstique;

/// <summary>
/// Draws the player, as the game draws them, into a picture.
///
/// The game already knows how to render a seraph in flat orthographic mode —
/// it is what the character screen shows — so this asks for exactly that, into a
/// buffer of its own rather than onto the screen, and reads the result back. What
/// comes out is the real thing: skin, hair, clothes, armour, whatever they are
/// wearing at that moment.
///
/// It must happen inside a frame, so this is a renderer that does nothing until
/// it is asked and then does it once. Registered in the GUI pass, which is the
/// state the character screen renders under.
///
/// Only the player's own client can do this. Nobody else's machine has that
/// seraph loaded, which is why the picture travels rather than the description.
/// </summary>
public sealed class PortraitCapture : IRenderer
{
    /// <summary>
    /// How large a picture to send. The map draws it at forty pixels; this is
    /// enough for a dense screen without being worth compressing further.
    /// </summary>
    public const int Size = 256;

    /// <summary>
    /// How large a canvas to draw into.
    ///
    /// Larger than the picture on purpose: where the seraph lands is not something
    /// this can know exactly, so it is given room to land anywhere and the picture
    /// is cut from wherever it did.
    /// </summary>
    private const int Canvas = 640;

    /// <summary>
    /// Where the seraph stands in the canvas, how tall, and how far back.
    ///
    /// A seraph does not appear at the position given: measured, it lands well over
    /// a hundred pixels to the right of it. Rather than work out the exact
    /// relationship and depend on it, the canvas is far larger than the picture and
    /// the figure is placed near its left, so it has room to land wherever it
    /// actually lands. What is kept is cut from wherever it did.
    /// </summary>
    private const double Origin = Canvas / 8.0;

    private const double HeadRoom = 80;

    private const double Depth = 250;

    private const float Stature = 260;

    /// <summary>
    /// Which way the seraph faces.
    ///
    /// Radians. A seraph drawn at zero stands in profile; a quarter turn back from
    /// that puts it face to the viewer.
    ///
    /// The character screen uses this same quarter turn plus three tenths of a
    /// radian, which is what gives it that three-quarter pose. That is styling for
    /// a screen somebody is looking at, and a face to be cut out of later wants to
    /// be square on, so the flourish is left off.
    /// </summary>
    private const float Facing = -1.5707963f;

    /// <summary>
    /// How much of a seraph, measured from the crown down, is its head.
    ///
    /// Three tenths, chosen by cutting a real one at several fractions and looking
    /// at the results: a quarter clips the jaw, a third gives away half the square
    /// to shoulder. This is the whole of the difference between a portrait and a
    /// photograph of somebody's coat.
    /// </summary>
    private const double HeadShare = 0.30;

    /// <summary>
    /// Where the light comes from: over the viewer's left shoulder.
    ///
    /// The value the game itself leaves `lightPosition` at, so it is both a
    /// sensible light and the right thing to put back afterwards.
    /// </summary>
    private static readonly Vec3f Light = new(0.7071068f, -0.7071068f, 0f);

    private readonly ICoreClientAPI _capi;
    private Action<byte[]?, string>? _then;

    public PortraitCapture(ICoreClientAPI capi)
    {
        _capi = capi;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "mapstique-portrait");
    }

    public double RenderOrder => 1.01;

    public int RenderRange => 0;

    /// <summary>Asks for a picture. The answer comes on the next frame.</summary>
    public void Take(Action<byte[]?, string> then) => _then = then;

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        var then = _then;
        if (then is null)
        {
            return;
        }
        _then = null;

        try
        {
            var png = Draw(dt, out var said);
            then(png, said);
        }
        catch (Exception error)
        {
            then(null, error.Message);
        }
    }

    /// <summary>
    /// One render, into a buffer nobody is looking at.
    ///
    /// The setup is the character screen's own, because that is the one place in
    /// the game that draws a seraph flat and it needs more than a framebuffer to
    /// do it: the GUI shader has to be the one in use, the model view matrix has
    /// to be pushed and tilted, and `lightPosition` has to point somewhere or the
    /// entity is lit by nothing. Drawn without them, it draws nothing at all —
    /// no error, just an empty picture, which is how this first went wrong.
    /// </summary>
    private byte[]? Draw(float dt, out string said)
    {
        var entity = _capi.World?.Player?.Entity;
        if (entity is null)
        {
            said = "there is no player to draw";
            return null;
        }

        var render = _capi.Render;
        var previous = render.CurrentFrameBuffer;
        FrameBufferRef? buffer = null;

        try
        {
            buffer = render.CreateFrameBuffer(Attributes());
            render.CurrentFrameBuffer = buffer;
            render.GlViewport(0, 0, Canvas, Canvas);

            // Transparent, so the card behind shows through around the seraph.
            // Depth is cleared with it; the entity is a solid thing and needs it.
            render.ClearFrameBuffer(buffer, new[] { 0f, 0f, 0f, 0f }, true, true);

            // True, not false. The flag chooses between two orthographic depth
            // ranges, and the shallow one is for flat GUI work at the very front —
            // a seraph stands four hundred units back and was being clipped away
            // entirely, which is a picture of nothing with nothing to report.
            // The game passes true wherever it draws something solid off screen.
            //
            // This also pushes both matrix stacks, so its partner is PerspectiveMode
            // and not a second call to itself. Pairing it with itself leaks two
            // stack entries per picture.
            render.OrthoMode(Canvas, Canvas, true);

            // Nothing else is touched. The character screen sets no depth, cull or
            // blend state of its own, which says `RenderEntityToGui` manages what
            // it needs — and every piece of state set here is a piece that has to
            // be put back exactly right for the rest of the frame to survive.

            // Whatever is drawing the rest of this frame stays drawing it. The
            // character screen never activates a shader — it writes to the one
            // already in use — and stopping one here left the game's own aim
            // renderer setting a uniform on a shader that was no longer active,
            // which ends the client. Activation is borrowed only when there is
            // nothing to borrow, and put back exactly as found either way.
            var before = render.CurrentActiveShader;
            var gui = render.GetEngineShader(EnumShaderProgram.Gui);
            var borrowed = before != gui;
            var wearing = borrowed ? "no gui shader was active" : "the gui shader was already active";

            if (borrowed)
            {
                gui.Use();
            }

            try
            {
                render.GlPushMatrix();
                render.GlTranslate(0, 0, 150);

                // The character screen tilts the view fourteen degrees down before
                // drawing, which is what makes a seraph there look like it is being
                // regarded rather than measured. Left off here for the same reason
                // the pose is square on: this picture is a face to be cut out.
                gui.Uniform("lightPosition", Light);

                // The character screen's own numbers, centred. Where a seraph
                // lands for a given position and size is not something worth
                // deriving twice, and these are known to put one on screen.
                render.RenderEntityToGui(
                    dt, entity, Origin, HeadRoom, Depth, Facing, Stature, ColorUtil.WhiteArgb);
            }
            finally
            {
                // The light goes back to the game's own before anything else, while
                // the shader it belongs to is still the one in use.
                gui.Uniform("lightPosition", Light);
                render.GlPopMatrix();

                if (borrowed)
                {
                    gui.Stop();
                    before?.Use();
                }
            }

            var picture = Frame(Readback.Bgra(Canvas, Canvas), out said);
            said = $"{said}, {wearing}";
            return picture;
        }
        finally
        {
            render.CurrentFrameBuffer = previous;
            if (buffer is not null)
            {
                render.DestroyFrameBuffer(buffer);
            }

            // Back to what the rest of the frame expects. PerspectiveMode pops
            // the two matrix stacks OrthoMode pushed, which puts the projection
            // back to whatever this interrupted rather than to a guess at it.
            render.PerspectiveMode();
            render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        }
    }

    /// <summary>
    /// Finds the seraph in the buffer and crops to its head and shoulders.
    ///
    /// Where in the canvas it lands depends on numbers this cannot know for
    /// certain — how tall the model is, what the GUI scale does, what a mod has
    /// changed — so rather than trusting a position, this looks for whatever was
    /// actually drawn and frames that. A picture with nothing in it is refused
    /// rather than sent: an empty portrait is indistinguishable from a working
    /// one until somebody opens the map and finds a hole in the card.
    /// </summary>
    private static byte[]? Frame(byte[] bgra, out string said)
    {
        int left = Canvas, right = -1, top = Canvas, bottom = -1;
        for (var y = 0; y < Canvas; y++)
        {
            for (var x = 0; x < Canvas; x++)
            {
                if (bgra[(y * Canvas + x) * 4 + 3] == 0)
                {
                    continue;
                }
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }

        if (right < 0)
        {
            said = $"the seraph drew nothing into a {Canvas}x{Canvas} canvas";
            return null;
        }

        var width = right - left + 1;
        var height = bottom - top + 1;

        // The head, which is the top of the figure: the first row drawn is the
        // crown, since the entity renderer turns the model half a turn about X and
        // this reads the rows back in the order they are stored.
        var band = System.Math.Max(1, (int)(height * HeadShare));
        var (headLeft, headRight) = ColumnsIn(bgra, top, top + band);

        // Centred on the head's own columns rather than the figure's. A seraph in a
        // coat is wider at the shoulder than at the ear, and centring on the whole
        // of it walks the face off to one side.
        var side = band;
        var x0 = (headLeft + headRight) / 2 - side / 2;
        var y0 = top - band / 12;

        // A figure touching an edge was cut off by it, and what is cropped from it
        // is a piece of a seraph rather than a small one. Worth saying: the
        // difference is invisible in the picture and decides where to look.
        var against = Edges(left, right, top, bottom);
        said = $"{width}x{height} found at {left},{top}, head {side}x{side} at {x0},{y0}{against}";
        return Encode(bgra, x0, y0, side);
    }

    /// <summary>How far left and right anything is drawn between two rows.</summary>
    private static (int Left, int Right) ColumnsIn(byte[] bgra, int from, int upto)
    {
        int left = Canvas, right = -1;
        for (var y = System.Math.Max(0, from); y < System.Math.Min(Canvas, upto); y++)
        {
            for (var x = 0; x < Canvas; x++)
            {
                if (bgra[(y * Canvas + x) * 4 + 3] == 0)
                {
                    continue;
                }
                if (x < left) left = x;
                if (x > right) right = x;
            }
        }

        return right < 0 ? (0, Canvas - 1) : (left, right);
    }

    /// <summary>Which sides of the canvas the figure ran off, if any.</summary>
    private static string Edges(int left, int right, int top, int bottom)
    {
        var against = new System.Collections.Generic.List<string>();
        if (left == 0) against.Add("left");
        if (right == Canvas - 1) against.Add("right");
        if (top == 0) against.Add("top");
        if (bottom == Canvas - 1) against.Add("bottom");
        return against.Count == 0 ? "" : $", CUT OFF at the {string.Join(" and ", against)}";
    }

    private static FramebufferAttrs Attributes() => new("mapstique-portrait", Canvas, Canvas)
    {
        Attachments = new[]
        {
            new FramebufferAttrsAttachment
            {
                AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                Texture = new RawTexture
                {
                    Width = Canvas,
                    Height = Canvas,
                    PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                    PixelFormat = EnumTexturePixelFormat.Rgba,
                    MinFilter = EnumTextureFilter.Linear,
                    MagFilter = EnumTextureFilter.Linear,
                    WrapS = EnumTextureWrap.ClampToEdge,
                    WrapT = EnumTextureWrap.ClampToEdge,
                },
            },
            new FramebufferAttrsAttachment
            {
                AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                Texture = new RawTexture
                {
                    Width = Canvas,
                    Height = Canvas,
                    PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32,
                    PixelFormat = EnumTexturePixelFormat.DepthComponent,
                    MinFilter = EnumTextureFilter.Nearest,
                    MagFilter = EnumTextureFilter.Nearest,
                    WrapS = EnumTextureWrap.ClampToEdge,
                    WrapT = EnumTextureWrap.ClampToEdge,
                },
            },
        },
    };

    /// <summary>
    /// One square of the canvas to a PNG, at the size sent.
    ///
    /// Row order is kept. A frame buffer's first row is conventionally its bottom
    /// one and this used to reverse them for that reason, which delivered every
    /// seraph standing on its head — so whatever this buffer is, it is not that.
    /// The evidence is a picture somebody looked at, which outranks the convention.
    /// </summary>
    private static byte[] Encode(byte[] bgra, int x0, int y0, int side)
    {
        var info = new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var cut = new SKBitmap(info);
        var rows = new byte[side * 4];
        for (var y = 0; y < side; y++)
        {
            var line = y0 + y;
            if (line < 0 || line >= Canvas)
            {
                continue;
            }

            // Only the part of the row that is inside the canvas. The rest of it
            // stays as the bitmap was born: transparent.
            var from = System.Math.Max(0, x0);
            var upto = System.Math.Min(Canvas, x0 + side);
            if (upto <= from)
            {
                continue;
            }

            System.Array.Clear(rows);
            System.Buffer.BlockCopy(bgra, (line * Canvas + from) * 4, rows, (from - x0) * 4, (upto - from) * 4);
            System.Runtime.InteropServices.Marshal.Copy(rows, 0, cut.GetPixels() + y * side * 4, side * 4);
        }

        using var scaled = cut.Resize(
            new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            SKSamplingOptions.Default);
        using var image = SKImage.FromBitmap(scaled ?? cut);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Reading pixels back off the graphics card.
///
/// The one thing the mod API has no call for: it will upload a texture and render
/// into one, but never hand the result back. The game does this with the same
/// OpenGL binding, so this does too.
///
/// Every OpenGL type stays inside the method body and none appears in a signature,
/// so nothing here is resolved until it is called — and it is only ever called on a
/// client. A dedicated server has no such library to find and never looks for one.
/// </summary>
internal static class Readback
{
    internal static byte[] Bgra(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        OpenTK.Graphics.OpenGL.GL.ReadPixels(
            0,
            0,
            width,
            height,
            OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
            OpenTK.Graphics.OpenGL.PixelType.UnsignedByte,
            pixels);
        return pixels;
    }
}
