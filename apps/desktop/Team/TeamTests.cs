using System.Collections.Concurrent;
using System.IO;
using PCBoost.Shared;

namespace PCBoost.Team;

// Dois clientes reais contra um PCBoost.Server em execução: PCBoost.exe --team-test http://localhost:5080
internal static class TeamTests
{
    public static async Task<string> Run(string server)
    {
        var log = new List<string>();
        static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
        static async Task Until(Func<bool> condition, string what) { for (int i = 0; i < 60 && !condition(); i++) await Task.Delay(100); Check(condition(), what); }
        static async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch (InvalidOperationException) { return true; } }
        string root = Path.Combine(Path.GetTempPath(), "pcboost-team-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "a")); Directory.CreateDirectory(Path.Combine(root, "b"));
        await using var a = new TeamClient(Path.Combine(root, "a"));
        await using var b = new TeamClient(Path.Combine(root, "b"));
        try
        {
            string tag = Guid.NewGuid().ToString("N")[..8];
            await a.SignIn(server, $"a-{tag}@pcboost.test", "senha-forte-123", "Alice", register: true, remember: false);
            await b.SignIn(server, $"b-{tag}@pcboost.test", "senha-forte-123", "Bruno", register: true, remember: true);
            log.Add("register: two accounts, hub connected");
            Check(await Fails(() => new TeamClient(Path.Combine(root, "a")).SignIn(server, $"a-{tag}@pcboost.test", "outra-senha-123", "X", true, false)), "duplicate e-mail must be rejected");
            Check(await Fails(() => new TeamClient(Path.Combine(root, "a")).SignIn(server, $"a-{tag}@pcboost.test", "senha-errada-1", null, false, false)), "wrong password must be rejected");
            log.Add("auth: duplicate e-mail and wrong password rejected");

            var group = await a.CreateGroup("Servidor de teste " + tag);
            var channels = await a.Channels(group.Id);
            Check(channels.Count(c => c.Kind == ChannelKind.Text) == 1 && channels.Any(c => c.Kind == ChannelKind.Voice) && channels.Any(c => c.Kind == ChannelKind.Board), "default #geral, voice and board channels");
            var text = channels.First(c => c.Kind == ChannelKind.Text); var voice = channels.First(c => c.Kind == ChannelKind.Voice); var board = channels.First(c => c.Kind == ChannelKind.Board);
            Check(await Fails(() => b.History(text.Id)), "non-member must not read history");
            int membersChanged = 0; a.MembersChanged += g => { if (g == group.Id) Interlocked.Increment(ref membersChanged); };
            var joined = await b.JoinGroup(group.Code.ToLowerInvariant());
            Check(joined.Id == group.Id && joined.Members == 2, "join by invite code");
            await Until(() => membersChanged > 0, "owner notified of new member");
            log.Add("servers: create with default channels, membership guard, invite code, member event");

            int channelsChanged = 0; b.ChannelsChanged += g => { if (g == group.Id) Interlocked.Increment(ref channelsChanged); };
            var extra = await a.CreateChannel(group.Id, "Jogos Online", ChannelKind.Text);
            Check(extra.Name == "jogos-online", "text channel names are slugged like #jogos-online");
            await Until(() => channelsChanged > 0, "members notified of new channel");
            Check(await Fails(() => b.CreateChannel(group.Id, "x", ChannelKind.Text)), "only owner creates channels");
            Check(await Fails(() => b.DeleteChannel(extra.Id)), "only owner deletes channels");
            await a.DeleteChannel(extra.Id);
            Check(await Fails(() => a.DeleteChannel(text.Id)), "last text channel cannot be deleted");
            log.Add("channels: owner create/delete, slug names, events, permission and last-text guards");

            var received = new ConcurrentQueue<MessageDto>(); b.Message += received.Enqueue;
            await a.Send(text.Id, "Olá do teste");
            await Until(() => received.Any(m => m.Text == "Olá do teste" && m.Author == "Alice" && m.ChannelId == text.Id), "message delivered with channel id");
            Check((await b.History(text.Id)).Any(m => m.Text == "Olá do teste"), "message persisted per channel");
            Check(await Fails(() => a.Send(voice.Id, "x")), "cannot chat in a voice channel");
            log.Add("chat: real-time per channel, history, channel-kind guard");

            var changed = new ConcurrentQueue<long>(); a.IdeaChanged += (g, c, i) => { if (c == board.Id) changed.Enqueue(i); };
            var idea = await b.SaveIdea(new IdeaInput(0, board.Id, "Ideia do Bruno", "Descrição", IdeaStatus.Backlog, IdeaKind.Idea, IdeaPriority.High, null));
            await Until(() => changed.Contains(idea.Id), "idea broadcast");
            await a.ToggleVote(idea.Id);
            Check(await a.Idea(idea.Id) is { Votes: 1, Voted: true }, "vote counted for voter");
            Check(await b.Idea(idea.Id) is { Votes: 1, Voted: false }, "vote flag is per user");
            var moved = await a.SaveIdea(new IdeaInput(idea.Id, board.Id, idea.Title, idea.Body, IdeaStatus.Doing, IdeaKind.Task, IdeaPriority.Normal, b.User!.Id));
            Check(moved is { Status: IdeaStatus.Doing, Assignee: "Bruno" }, "move + assign");
            Check(await Fails(() => a.SaveIdea(new IdeaInput(idea.Id, board.Id, "x", "", "inexistente", IdeaKind.Idea, IdeaPriority.Normal, null))), "invalid status rejected");
            Check((await a.Ideas(board.Id)).Count == 1, "board lists its own items");
            log.Add("board: create, broadcast, per-user vote, move/assign, validation");

            var calls = new ConcurrentQueue<(long Channel, List<CallParticipant> List)>(); b.CallChanged += (g, c, p) => calls.Enqueue((c, p));
            var audio = new ConcurrentQueue<(long User, byte[] Frame)>(); b.Audio += (u, f) => audio.Enqueue((u, f));
            await a.JoinCall(voice.Id);
            var inCall = await b.JoinCall(voice.Id, muted: true);
            Check(inCall.Count == 2 && inCall.Any(p => p.UserId == b.User!.Id && p.Muted), "two participants, join muted");
            Check((await a.CallStates(group.Id)).Single(v => v.ChannelId == voice.Id).Participants.Count == 2, "server reports voice state per channel");
            Check(await Fails(() => a.JoinCall(text.Id)), "cannot join a text channel as voice");
            byte[] frame = Enumerable.Range(0, 1280).Select(i => (byte)(i * 7)).ToArray();
            await a.SendAudio(frame);
            await Until(() => audio.Any(x => x.User == a.User!.Id && x.Frame.SequenceEqual(frame)), "audio frame relayed byte-for-byte");
            await a.SetMuted(true);
            await Until(() => calls.Any(x => x.Channel == voice.Id && x.List.Any(p => p.UserId == a.User!.Id && p.Muted)), "mute state broadcast");
            await a.LeaveCall();
            await Until(() => calls.Any(x => x.Channel == voice.Id && x.List.Count == 1 && x.List[0].UserId == b.User!.Id), "leave call broadcast");
            log.Add("voice: per-channel join (muted), states, PCM relay, mute broadcast, leave");

            var presence = new ConcurrentQueue<(long User, bool Online)>(); b.Presence += (g, u, on) => presence.Enqueue((u, on));
            long aliceId = a.User!.Id;
            await a.DisposeAsync();
            await Until(() => presence.Contains((aliceId, false)), "offline presence broadcast");
            log.Add("presence: offline on disconnect");

            var resumed = new TeamClient(Path.Combine(root, "b"));
            Check(await resumed.Resume(), "saved (DPAPI) session resumes");
            await resumed.SignOut();
            Check(!await new TeamClient(Path.Combine(root, "b")).Resume(), "signed-out token no longer valid");
            log.Add("session: DPAPI resume and logout revocation");
            return "PASS: " + string.Join("; ", log);
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}
