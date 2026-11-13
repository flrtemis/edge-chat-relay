using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace EdgeChatRelay
{
    /// <summary>
    /// The conduit logic. When armed, every chatbot response captured in one panel
    /// is staged in the middle CONDUIT BOX (visible, editable), then typed into the
    /// other panel's chat input and sent. The two chatbots therefore talk to each
    /// other strictly through a text clipboard that connects to nothing — with the
    /// user free to walk away, edit messages mid-flight, or interject at will.
    ///
    /// Guarantees: one forward in flight at a time, pacing delay (also the
    /// in-flight edit window), draft protection for both the panels and the
    /// conduit box, and a max-turns circuit breaker.
    /// </summary>
    public class RelayEngine
    {
        private readonly MainForm _form;
        private readonly Queue<(int from, string text)> _queue = new();
        private bool _busy;

        public bool Armed;
        public int MaxTurns = 50;      // 0 = unlimited
        public int DelayMs = 2500;     // pacing + in-flight edit window
        public int TurnsDone;

        public event Action<string>? RelayStatus;
        public event Action? TurnsChanged;

        public RelayEngine(MainForm form)
        {
            _form = form;
        }

        public void Arm()
        {
            Armed = true;
            RelayStatus?.Invoke("Relay ARMED — responses now pass through the conduit box to the other panel.");
        }

        public void Stop(string reason)
        {
            Armed = false;
            _queue.Clear();
            RelayStatus?.Invoke("Relay stopped: " + reason);
        }

        public void OnResponse(int panelId, string text)
        {
            if (!Armed) return;
            _queue.Enqueue((panelId, text));
            _ = PumpAsync();
        }

        private async Task PumpAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                while (_queue.Count > 0 && Armed)
                {
                    if (MaxTurns > 0 && TurnsDone >= MaxTurns)
                    {
                        Stop("max turns (" + MaxTurns + ") reached");
                        break;
                    }

                    var (from, text) = _queue.Dequeue();
                    int to = 1 - from;
                    var conduit = _form.Conduit;

                    // If the conduit box holds the user's own text (a draft or an
                    // interjection), never clobber it — wait, then give up politely.
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (conduit != null && conduit.IsDirty && Armed && DateTime.UtcNow < deadline)
                    {
                        RelayStatus?.Invoke("Conduit box holds your text — relay waiting (send or clear it to continue)…");
                        await Task.Delay(2000);
                    }
                    if (!Armed) break;
                    if (conduit != null && conduit.IsDirty)
                    {
                        conduit.AppendTransit($"[held] P{from + 1}→P{to + 1}: conduit box in use; kept in panel log only");
                        continue;
                    }

                    // Stage the message in the middle box — the visible pass-through.
                    conduit?.SetTransitText(text, from + 1, to + 1);
                    RelayStatus?.Invoke($"Panel {from + 1} → Panel {to + 1}: in conduit box — {DelayMs / 1000.0:0.#}s edit window (what's in the box is what gets sent).");

                    if (DelayMs > 0)
                        await Task.Delay(DelayMs);

                    // Whatever is in the box now goes out — original or user-edited.
                    string finalText = conduit != null ? conduit.BoxText : text;
                    if (finalText.Trim().Length == 0)
                    {
                        conduit?.AppendTransit($"[deleted] P{from + 1}→P{to + 1}: message erased in conduit box");
                        continue;
                    }

                    var target = _form.Panels[to];
                    bool sent = false;

                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        var resultJson = await target.InjectAndSendAsync(finalText, DelayMs);
                        if (ParseOk(resultJson))
                        {
                            sent = true;
                            RelayStatus?.Invoke($"Panel {from + 1} → Panel {to + 1}: sent ({ParseMethod(resultJson)}).");
                            break;
                        }
                        if (ParseDetail(resultJson).Contains("busy"))
                        {
                            RelayStatus?.Invoke($"Panel {to + 1} input busy (draft present) — waiting…");
                            await Task.Delay(5000);
                            continue;
                        }
                        RelayStatus?.Invoke($"Panel {to + 1}: {ParseDetail(resultJson)}");
                        break;
                    }

                    if (sent)
                    {
                        TurnsDone++;
                        TurnsChanged?.Invoke();
                        conduit?.MarkSent($"P{from + 1} → P{to + 1}  {OneLine(finalText)}");
                    }
                    else
                    {
                        conduit?.AppendTransit($"[failed] P{from + 1}→P{to + 1}  {OneLine(finalText)}");
                    }
                }
            }
            catch (Exception ex)
            {
                RelayStatus?.Invoke("Relay error: " + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private static string OneLine(string s)
        {
            var n = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return n.Length > 90 ? n.Substring(0, 90) + "…" : n;
        }

        private static bool ParseOk(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                return d.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
            }
            catch { return false; }
        }

        private static string ParseMethod(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                return d.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "?" : "?";
            }
            catch { return "?"; }
        }

        private static string ParseDetail(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                return d.RootElement.TryGetProperty("detail", out var v) ? v.GetString() ?? "" : "";
            }
            catch { return ""; }
        }
    }
}
