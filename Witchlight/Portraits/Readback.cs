namespace Witchlight;

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
