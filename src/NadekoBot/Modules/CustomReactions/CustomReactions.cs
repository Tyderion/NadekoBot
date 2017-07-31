using System.Linq;
using System.Threading.Tasks;
using Discord.Commands;
using NadekoBot.Services;
using NadekoBot.Services.Database.Models;
using Discord;
using NadekoBot.Extensions;
using Discord.WebSocket;
using System;
using System.Text.RegularExpressions;
using NadekoBot.Common.Attributes;
using NadekoBot.Modules.CustomReactions.Services;
using NadekoBot.Modules.CustomReactions.Extensions;

namespace NadekoBot.Modules.CustomReactions
{
    public class CustomReactions : NadekoTopLevelModule<CustomReactionsService>
    {
        private readonly IBotCredentials _creds;
        private readonly DbService _db;
        private readonly DiscordSocketClient _client;

        public CustomReactions(IBotCredentials creds, DbService db,
            DiscordSocketClient client)
        {
            _creds = creds;
            _db = db;
            _client = client;
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task AddCustRegexReact(string key, [Remainder] string message)
        {
            if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(key))
                return;

            Match isRegex = Regex.Match(key, "^/(.+)/$");
            if (!isRegex.Success)
            {
                await AddCustReact(key, message);
            } else
            {

                var cr = new CustomReaction()
                {
                    IsRegex = isRegex.Success,
                    Trigger = isRegex.Success ? isRegex.Groups.Cast<Group>().Skip(1).Single().Value : key,
                    Response = message,
                };

                await addCustomReaction(cr);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task AddCustReact(string key, [Remainder] string message)
        {
            if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(key))
                return;

            key = key.ToLowerInvariant();
            var cr = new CustomReaction()
            {
                IsRegex = false,
                Trigger = key,
                Response = message,
            };

            await addCustomReaction(cr);
        }

        private async Task addCustomReaction(CustomReaction cr)
        {
            var channel = Context.Channel as ITextChannel;

            cr.GuildId = channel?.Guild.Id;

            if ((channel == null && !_creds.IsOwner(Context.User)) || (channel != null && !((IGuildUser)Context.User).GuildPermissions.Administrator))
            {
                await ReplyErrorLocalized("insuff_perms").ConfigureAwait(false);
                return;
            }
            using (var uow = _db.UnitOfWork)
            {
                uow.CustomReactions.Add(cr);

                await uow.CompleteAsync().ConfigureAwait(false);
            }

            if (channel == null)
            {
                Array.Resize(ref _service.GlobalReactions, _service.GlobalReactions.Length + 1);
                _service.GlobalReactions[_service.GlobalReactions.Length - 1] = cr;
            }
            else
            {
                _service.GuildReactions.AddOrUpdate(Context.Guild.Id,
                    new CustomReaction[] { cr },
                    (k, old) =>
                    {
                        Array.Resize(ref old, old.Length + 1);
                        old[old.Length - 1] = cr;
                        return old;
                    });
            }

            await Context.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                .WithTitle(GetText("new_cust_react"))
                .WithDescription(cr.DisplayId())
                .AddField(efb => efb.WithName(GetText("trigger")).WithValue(cr.Trigger))
                .AddField(efb => efb.WithName(GetText("response")).WithValue(cr.Response.Length > 1024 ? GetText("redacted_too_long") : cr.Response))
                ).ConfigureAwait(false);
        }

        [NadekoCommand, Usage, Description, Aliases]
        [Priority(1)]
        public async Task ListCustReact(int page = 1)
        {
            if (--page < 0 || page > 999)
                return;
            CustomReaction[] customReactions;
            if (Context.Guild == null)
                customReactions = _service.GlobalReactions.Where(cr => cr != null).ToArray();
            else
                customReactions = _service.GuildReactions.GetOrAdd(Context.Guild.Id, Array.Empty<CustomReaction>()).Where(cr => cr != null).ToArray();

            if (customReactions == null || !customReactions.Any())
            {
                await ReplyErrorLocalized("no_found").ConfigureAwait(false);
                return;
            }

            var lastPage = customReactions.Length / 20;
            await Context.Channel.SendPaginatedConfirmAsync(_client, page, curPage =>
                new EmbedBuilder().WithOkColor()
                    .WithTitle(GetText("name"))
                    .WithDescription(string.Join("\n", customReactions.OrderBy(cr => cr.Trigger)
                                                    .Skip(curPage * 20)
                                                    .Take(20)
                                                    .Select(cr =>
                                                    {
                                                        var str = $"`{cr.DisplayId()}` {cr.Trigger}";
                                                        if (cr.AutoDeleteTrigger)
                                                        {
                                                            str = "🗑" + str;
                                                        }
                                                        if (cr.DmResponse)
                                                        {
                                                            str = "📪" + str;
                                                        }
                                                        return str;
                                                    }))), lastPage)
                                .ConfigureAwait(false);
        }

        public enum All
        {
            All
        }

        [NadekoCommand, Usage, Description, Aliases]
        [Priority(0)]
        public async Task ListCustReact(All x)
        {
            CustomReaction[] customReactions;
            if (Context.Guild == null)
                customReactions = _service.GlobalReactions.Where(cr => cr != null).ToArray();
            else
                customReactions = _service.GuildReactions.GetOrAdd(Context.Guild.Id, new CustomReaction[]{ }).Where(cr => cr != null).ToArray();

            if (customReactions == null || !customReactions.Any())
            {
                await ReplyErrorLocalized("no_found").ConfigureAwait(false);
                return;
            }

            var txtStream = await customReactions.GroupBy(cr => cr.Trigger)
                                                        .OrderBy(cr => cr.Key)
                                                        .Select(cr => new { Trigger = cr.Key, Responses = cr.Select(y => new { id = y.Id, text = y.Response }).ToList() })
                                                        .ToJson()
                                                        .ToStream()
                                                        .ConfigureAwait(false);

            if (Context.Guild == null) // its a private one, just send back
                await Context.Channel.SendFileAsync(txtStream, "customreactions.txt", GetText("list_all")).ConfigureAwait(false);
            else
                await ((IGuildUser)Context.User).SendFileAsync(txtStream, "customreactions.txt", GetText("list_all")).ConfigureAwait(false);
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task ListCustReactG(int page = 1)
        {
            if (--page < 0 || page > 9999)
                return;
            CustomReaction[] customReactions;
            if (Context.Guild == null)
                customReactions = _service.GlobalReactions.Where(cr => cr != null).ToArray();
            else
                customReactions = _service.GuildReactions.GetOrAdd(Context.Guild.Id, new CustomReaction[]{ }).Where(cr => cr != null).ToArray();

            if (customReactions == null || !customReactions.Any())
            {
                await ReplyErrorLocalized("no_found").ConfigureAwait(false);
            }
            else
            {
                var ordered = customReactions
                    .GroupBy(cr => cr.Trigger)
                    .OrderBy(cr => cr.Key)
                    .ToList();

                var lastPage = ordered.Count / 20;
                await Context.Channel.SendPaginatedConfirmAsync(_client, page, (curPage) =>
                    new EmbedBuilder().WithOkColor()
                        .WithTitle(GetText("name"))
                        .WithDescription(string.Join("\r\n", ordered
                                                         .Skip(curPage * 20)
                                                         .Take(20)
                                                         .Select(cr => $"**{cr.Key.Trim().ToLowerInvariant()}** `x{cr.Count()}`"))), lastPage)
                             .ConfigureAwait(false);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task ShowCustReact(String id)
        {
            int parsedId = int.Parse(id.Replace("r", ""));
            CustomReaction[] customReactions;
            if (Context.Guild == null)
                customReactions = _service.GlobalReactions;
            else
                customReactions = _service.GuildReactions.GetOrAdd(Context.Guild.Id, new CustomReaction[]{ });

            var found = customReactions.FirstOrDefault(cr => cr?.Id == parsedId);

            if (found == null)
            {
                await ReplyErrorLocalized("no_found_id").ConfigureAwait(false);
                return;
            }
            else
            {
                await Context.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                    .WithDescription(found.DisplayId())
                    .AddField(efb => efb.WithName(GetText("trigger")).WithValue(found.Trigger))
                    .AddField(efb => efb.WithName(GetText("response")).WithValue(found.Response + "\n```css\n" + found.Response + "```"))
                    ).ConfigureAwait(false);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task DelCustReact(string id)
        {
            if ((Context.Guild == null && !_creds.IsOwner(Context.User)) || (Context.Guild != null && !((IGuildUser)Context.User).GuildPermissions.Administrator))
            {
                await ReplyErrorLocalized("insuff_perms").ConfigureAwait(false);
                return;
            }

            var success = false;
            CustomReaction toDelete;
            int parsedId = int.Parse(id.Replace("r", ""));
            using (var uow = _db.UnitOfWork)
            {
                toDelete = uow.CustomReactions.Get(parsedId);
                if (toDelete == null) //not found
                    success = false;
                else
                {
                    if ((toDelete.GuildId == null || toDelete.GuildId == 0) && Context.Guild == null)
                    {
                        uow.CustomReactions.Remove(toDelete);
                        //todo 91 i can dramatically improve performance of this, if Ids are ordered.
                        _service.GlobalReactions = _service.GlobalReactions.Where(cr => cr?.Id != toDelete.Id).ToArray();
                        success = true;
                    }
                    else if ((toDelete.GuildId != null && toDelete.GuildId != 0) && Context.Guild.Id == toDelete.GuildId)
                    {
                        uow.CustomReactions.Remove(toDelete);
                        _service.GuildReactions.AddOrUpdate(Context.Guild.Id, new CustomReaction[] { }, (key, old) =>
                        {
                            return old.Where(cr => cr?.Id != toDelete.Id).ToArray();
                        });
                        success = true;
                    }
                    if (success)
                        await uow.CompleteAsync().ConfigureAwait(false);
                }
            }

            if (success)
            {
                await Context.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                    .WithTitle(GetText("deleted"))
                    .WithDescription(toDelete.DisplayId())
                    .AddField(efb => efb.WithName(GetText("trigger")).WithValue(toDelete.Trigger))
                    .AddField(efb => efb.WithName(GetText("response")).WithValue(toDelete.Response)));
            }
            else
            {
                await ReplyErrorLocalized("no_found_id").ConfigureAwait(false);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task CrCa(int id)
        {
            if ((Context.Guild == null && !_creds.IsOwner(Context.User)) ||
                (Context.Guild != null && !((IGuildUser)Context.User).GuildPermissions.Administrator))
            {
                await ReplyErrorLocalized("insuff_perms").ConfigureAwait(false);
                return;
            }

            CustomReaction[] reactions = new CustomReaction[0];

            if (Context.Guild == null)
                reactions = _service.GlobalReactions;
            else
            {
                _service.GuildReactions.TryGetValue(Context.Guild.Id, out reactions);
            }
            if (reactions.Any())
            {
                var reaction = reactions.FirstOrDefault(x => x.Id == id);

                if (reaction == null)
                {
                    await ReplyErrorLocalized("no_found_id").ConfigureAwait(false);
                    return;
                }

                var setValue = reaction.ContainsAnywhere = !reaction.ContainsAnywhere;

                using (var uow = _db.UnitOfWork)
                {
                    uow.CustomReactions.Get(id).ContainsAnywhere = setValue;
                    uow.Complete();
                }

                if (setValue)
                {
                    await ReplyConfirmLocalized("crca_enabled", Format.Code(reaction.Id.ToString())).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalized("crca_disabled", Format.Code(reaction.Id.ToString())).ConfigureAwait(false);
                }
            }
            else
            {
                await ReplyErrorLocalized("no_found").ConfigureAwait(false);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task CrDm(int id)
        {
            if ((Context.Guild == null && !_creds.IsOwner(Context.User)) || 
                (Context.Guild != null && !((IGuildUser)Context.User).GuildPermissions.Administrator))
            {
                await ReplyErrorLocalized("insuff_perms").ConfigureAwait(false);
                return;
            }

            CustomReaction[] reactions = new CustomReaction[0];

            if (Context.Guild == null)
                reactions = _service.GlobalReactions;
            else
            {
                _service.GuildReactions.TryGetValue(Context.Guild.Id, out reactions);
            }
            if (reactions.Any())
            {
                var reaction = reactions.FirstOrDefault(x => x.Id == id);

                if (reaction == null)
                {
                    await ReplyErrorLocalized("no_found_id").ConfigureAwait(false);
                    return;
                }

                var setValue = reaction.DmResponse = !reaction.DmResponse;

                using (var uow = _db.UnitOfWork)
                {
                    uow.CustomReactions.Get(id).DmResponse = setValue;
                    uow.Complete();
                }

                if (setValue)
                {
                    await ReplyConfirmLocalized("crdm_enabled", Format.Code(reaction.Id.ToString())).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalized("crdm_disabled", Format.Code(reaction.Id.ToString())).ConfigureAwait(false);
                }
            }
            else
            {
                await ReplyErrorLocalized("no_found").ConfigureAwait(false);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task CrAd(int id)
        {
            if ((Context.Guild == null && !_creds.IsOwner(Context.User)) ||
                (Context.Guild != null && !((IGuildUser)Context.User).GuildPermissions.Administrator))
            {
                await ReplyErrorLocalized("insuff_perms").ConfigureAwait(false);
                return;
            }

            CustomReaction[] reactions = new CustomReaction[0];

            if (Context.Guild == null)
                reactions = _service.GlobalReactions;
            else
            {
                _service.GuildReactions.TryGetValue(Context.Guild.Id, out reactions);
            }
            if (reactions.Any())
            {
                var reaction = reactions.FirstOrDefault(x => x.Id == id);

                if (reaction == null)
                {
                    await ReplyErrorLocalized("no_found_id").ConfigureAwait(false);
                    return;
                }

                var setValue = reaction.AutoDeleteTrigger = !reaction.AutoDeleteTrigger;

                using (var uow = _db.UnitOfWork)
                {
                    uow.CustomReactions.Get(id).AutoDeleteTrigger = setValue;
                    uow.Complete();
                }

                if (setValue)
                {
                    await ReplyConfirmLocalized("crad_enabled", Format.Code(reaction.Id.ToString())).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalized("crad_disabled", Format.Code(reaction.Id.ToString())).ConfigureAwait(false);
                }
            }
            else
            {
                await ReplyErrorLocalized("no_found").ConfigureAwait(false);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        [OwnerOnly]
        public async Task CrStatsClear(string trigger = null)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                _service.ClearStats();
                await ReplyConfirmLocalized("all_stats_cleared").ConfigureAwait(false);
            }
            else
            {
                if (_service.ReactionStats.TryRemove(trigger, out _))
                {
                    await ReplyErrorLocalized("stats_cleared", Format.Bold(trigger)).ConfigureAwait(false);
                }
                else
                {
                    await ReplyErrorLocalized("stats_not_found").ConfigureAwait(false);
                }
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        public async Task CrStats(int page = 1)
        {
            if (--page < 0)
                return;
            var ordered = _service.ReactionStats.OrderByDescending(x => x.Value).ToArray();
            if (!ordered.Any())
                return;
            var lastPage = ordered.Length / 9;
            await Context.Channel.SendPaginatedConfirmAsync(_client, page,
                (curPage) => ordered.Skip(curPage * 9)
                                    .Take(9)
                                    .Aggregate(new EmbedBuilder().WithOkColor().WithTitle(GetText("stats")),
                                            (agg, cur) => agg.AddField(efb => efb.WithName(cur.Key).WithValue(cur.Value.ToString()).WithIsInline(true))), lastPage)
                .ConfigureAwait(false);
        }
    }
}