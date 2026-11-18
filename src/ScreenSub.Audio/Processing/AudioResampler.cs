namespace ScreenSub.Audio.Processing;

/// <summary>
/// High performance audio resampler and channel mixer.
/// Converts any source audio format (sample rate, channels, bit depth) to standard 16kHz 16-bit signed Mono PCM.
/// </summary>
public static class AudioResampler
{
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int TargetBitsPerSample = 16;

    /// <summary>
    /// Converts a raw audio buffer from WASAPI loopback into 16kHz 16-bit mono PCM.
    /// </summary>
    public static byte[] ResampleTo16kMonoPcm(
        byte[] sourceBuffer,
        int bytesRecorded,
        int sourceSampleRate,
        int sourceChannels,
        int sourceBitsPerSample,
        bool isFloat)
    {
        if (bytesRecorded <= 0 || sourceBuffer.Length == 0)
        {
            return Array.Empty<byte>();
        }

        // 1. Decode to normalized float samples (-1.0 to 1.0) and mix down to mono
        int bytesPerSample = sourceBitsPerSample / 8;
        int frameSize = bytesPerSample * sourceChannels;
        int totalFrames = bytesRecorded / frameSize;

        if (totalFrames <= 0)
        {
            return Array.Empty<byte>();
        }

        float[] monoSamples = new float[totalFrames];

        if (isFloat && sourceBitsPerSample == 32)
        {
            for (int i = 0; i < totalFrames; i++)
            {
                int frameOffset = i * frameSize;
                float sum = 0f;
                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    float sample = BitConverter.ToSingle(sourceBuffer, frameOffset + ch * 4);
                    sum += sample;
                }
                monoSamples[i] = sum / sourceChannels;
            }
        }
        else if (!isFloat && sourceBitsPerSample == 16)
        {
            for (int i = 0; i < totalFrames; i++)
            {
                int frameOffset = i * frameSize;
                float sum = 0f;
                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    short sample = BitConverter.ToInt16(sourceBuffer, frameOffset + ch * 2);
                    sum += sample / 32768f;
                }
                monoSamples[i] = sum / sourceChannels;
            }
        }
        else if (!isFloat && sourceBitsPerSample == 32)
        {
            for (int i = 0; i < totalFrames; i++)
            {
                int frameOffset = i * frameSize;
                float sum = 0f;
                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    int sample = BitConverter.ToInt32(sourceBuffer, frameOffset + ch * 4);
                    sum += sample / 2147483648f;
                }
                monoSamples[i] = sum / sourceChannels;
            }
        }
        else
        {
            // Fallback for 24-bit PCM or other formats
            for (int i = 0; i < totalFrames; i++)
            {
                int frameOffset = i * frameSize;
                float sum = 0f;
                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    if (sourceBitsPerSample == 24)
                    {
                        int sample = (sourceBuffer[frameOffset + ch * 3 + 2] << 16) |
                                     (sourceBuffer[frameOffset + ch * 3 + 1] << 8) |
                                      sourceBuffer[frameOffset + ch * 3];
                        if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                        sum += sample / 8388608f;
                    }
                    else
                    {
                        sum += 0f;
                    }
                }
                monoSamples[i] = sum / sourceChannels;
            }
        }

        // 2. Resample mono float array from sourceSampleRate to TargetSampleRate (16,000 Hz)
        if (sourceSampleRate == TargetSampleRate)
        {
            return ConvertFloatTo16BitPcm(monoSamples);
        }

        double ratio = (double)sourceSampleRate / TargetSampleRate;
        int targetLength = (int)(totalFrames / ratio);
        float[] resampled = new float[targetLength];

        for (int i = 0; i < targetLength; i++)
        {
            double sourceIndex = i * ratio;
            int indexFloor = (int)sourceIndex;
            int indexCeil = Math.Min(indexFloor + 1, totalFrames - 1);
            double frac = sourceIndex - indexFloor;

            // Linear interpolation
            resampled[i] = (float)((1.0 - frac) * monoSamples[indexFloor] + frac * monoSamples[indexCeil]);
        }

        return ConvertFloatTo16BitPcm(resampled);
    }

    private static byte[] ConvertFloatTo16BitPcm(float[] samples)
    {
        byte[] pcm16 = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float val = Math.Clamp(samples[i], -1.0f, 1.0f);
            short sample16 = (short)(val * 32767f);
            pcm16[i * 2] = (byte)(sample16 & 0xFF);
            pcm16[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }
        return pcm16;
    }
}

