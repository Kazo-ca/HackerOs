namespace HackerOs.Platform.Core.Sounds;

/// <summary>
/// The sound card of the OS (ADR 0204 section 5): plays one decoded clip. The browser host plays it
/// with the Web Audio API; tests record the calls.
/// </summary>
public interface IOsSoundOutput
{
    /// <summary>Plays a clip once, mixed over any clip still playing.</summary>
    /// <param name="clip">The clip: a cache key and a way to read its bytes when the key is not cached yet.</param>
    /// <param name="volume">Gain from 0 to 1.</param>
    /// <param name="cancellationToken">Cancels the read of the bytes.</param>
    /// <returns>
    /// <see langword="true"/> when the clip started; <see langword="false"/> when the host cannot play
    /// it (no audio device, the browser still waits for a first click, or the data does not decode).
    /// </returns>
    ValueTask<bool> PlayAsync(OsSoundClip clip, double volume, CancellationToken cancellationToken = default);
}

/// <summary>One sound file to play.</summary>
/// <param name="Key">
/// Cache key: the file path and its revision, so an edited file is decoded again and an unchanged
/// one is not read twice.
/// </param>
/// <param name="MediaType"><c>audio/ogg</c> or <c>audio/mpeg</c>.</param>
/// <param name="LoadAsync">Reads the file's bytes.</param>
public sealed record OsSoundClip(string Key, string MediaType, Func<CancellationToken, ValueTask<byte[]>> LoadAsync);
