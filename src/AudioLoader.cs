using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayerRadio
{
    /// <summary>Loads local audio files with UnityWebRequestMultimedia (file:// URIs).</summary>
    public static class AudioLoader
    {
        public static readonly string[] Extensions = { ".mp3", ".wav", ".ogg" };

        public static AudioType TypeFor(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".mp3": return AudioType.MPEG;
                default: return AudioType.UNKNOWN;
            }
        }

        public static IEnumerator Load(string path, Action<AudioClip, string> done)
        {
            string uri;
            try { uri = new Uri(Path.GetFullPath(path)).AbsoluteUri; }
            catch (Exception e) { done(null, "bad path: " + e.Message); yield break; }

            using (var req = UnityWebRequestMultimedia.GetAudioClip(uri, TypeFor(Path.GetExtension(path))))
            {
                if (req.downloadHandler is DownloadHandlerAudioClip dh)
                {
                    dh.streamAudio = false;  // fully decoded => accurate AudioSource.time seeking
                    dh.compressed = false;
                }
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    done(null, req.error);
                    yield break;
                }

                AudioClip clip = null;
                string err = null;
                try { clip = DownloadHandlerAudioClip.GetContent(req); }
                catch (Exception e) { err = e.Message; }

                if (clip == null || clip.length <= 0f)
                {
                    if (clip != null) UnityEngine.Object.Destroy(clip);
                    done(null, err ?? "decoded clip is empty (unsupported format?)");
                    yield break;
                }
                clip.name = "PlayerRadio:" + Path.GetFileName(path);
                done(clip, null);
            }
        }
    }
}
