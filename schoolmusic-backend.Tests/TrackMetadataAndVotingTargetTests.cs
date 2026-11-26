using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public sealed class TrackMetadataAndVotingTargetTests
{
    private static async Task<string> LoginAsAsync(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { name = username, password });
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(dto);
        return dto.jwtSecurityToken;
    }

    [Fact]
    public async Task TracksAndFavourites_ExposeConsistentSpotifyMetadata_StableSongId_AndAuthIsFavorite()
    {
        using var factory = new TestWebApplicationFactory();
        var admin = await factory.SeedUserAsync("track_admin", "AdminPass123!", rankId: 3);
        var studentA = await factory.SeedUserAsync("student_a", "StudentPass123!", rankId: 1);
        var studentB = await factory.SeedUserAsync("student_b", "StudentPass123!", rankId: 1);

        const string spotifyTrackId = "3n3Ppam7vgaVa1iaRUc9Lp";
        factory.SongSourceMock
            .Setup(s => s.GetTrackByIdAsync(spotifyTrackId))
            .ReturnsAsync(new ExternalTrackDto(
                ExternalId: spotifyTrackId,
                Title: "Mr. Brightside",
                ArtistName: "The Killers",
                CoverUrl: "https://cdn.example/cover.jpg",
                DurationMs: 222000,
                PreviewUrl: "https://p.scdn.co/mp3-preview/sample",
                Source: "spotify"));

        using var adminClient = factory.CreateClient();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();

        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsAsync(adminClient, admin.UserLogin, "AdminPass123!"));
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsAsync(clientA, studentA.UserLogin, "StudentPass123!"));
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsAsync(clientB, studentB.UserLogin, "StudentPass123!"));

        // 1. Student A dodaje utwór ze Spotify do ulubionych, powtórzenie zwraca 409 Conflict,
        //    a dodanie przez innego użytkownika z użyciem URI spotify:track:... zachowuje stabilne songId
        var firstFav = await clientA.PostAsync($"/api/tracks/spotify/{spotifyTrackId}/favourites", null);
        firstFav.EnsureSuccessStatusCode();
        var firstDto = await firstFav.Content.ReadFromJsonAsync<FavouriteMutationResponseDto>();
        Assert.NotNull(firstDto);

        var duplicateFav = await clientA.PostAsync($"/api/tracks/spotify/spotify:track:{spotifyTrackId}/favourites", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicateFav.StatusCode);

        // 2. Zalogowany Student A widzi isFavorite=true w /api/tracks i pełne metadane w /api/tracks/favourites;
        //    Student B widzi isFavorite=false, a po polubieniu dostaje to samo stabilne songId.
        var tracksA = await (await clientA.GetAsync("/api/tracks"))
            .Content.ReadFromJsonAsync<TracksPagedResponseDto>();
        var itemA = Assert.Single(tracksA!.Items);
        Assert.Equal(firstDto.SongId, itemA.Id);
        Assert.Equal("The Killers", itemA.ArtistName);
        Assert.Equal(spotifyTrackId, itemA.SpotifyId);
        Assert.Equal("spotify", itemA.Source);
        Assert.True(itemA.IsFavorite);

        var tracksB = await (await clientB.GetAsync("/api/tracks"))
            .Content.ReadFromJsonAsync<TracksPagedResponseDto>();
        Assert.False(Assert.Single(tracksB!.Items).IsFavorite);

        var secondUserFav = await clientB.PostAsync($"/api/tracks/spotify/spotify:track:{spotifyTrackId}/favourites", null);
        secondUserFav.EnsureSuccessStatusCode();
        var secondDto = await secondUserFav.Content.ReadFromJsonAsync<FavouriteMutationResponseDto>();
        Assert.Equal(firstDto.SongId, secondDto!.SongId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            Assert.Equal(1, await db.Songs.CountAsync(s => s.SpotifyId == spotifyTrackId));
        }

        var favsA = await (await clientA.GetAsync("/api/tracks/favourites"))
            .Content.ReadFromJsonAsync<FavouriteTracksResponseDto>();
        var favItem = Assert.Single(favsA!.Tracks);
        Assert.Equal(firstDto.SongId, favItem.SongId);
        Assert.Equal("The Killers", favItem.ArtistName);
        Assert.Equal(spotifyTrackId, favItem.SpotifyId);
        Assert.Equal("spotify", favItem.Source);
        Assert.Equal("https://p.scdn.co/mp3-preview/sample", favItem.SongUrl);

        // 3. Próba utworzenia duplikatu przez AdminTracksController z linkiem URL Spotify -> 409 Conflict ProblemDetails
        var duplicateResp = await adminClient.PostAsJsonAsync("/api/admin/tracks", new CreateTrackDto(
            Title: "Mr. Brightside Duplicate",
            ArtistName: "The Killers",
            Genre: "Rock",
            DurationMs: 222000,
            SongUrl: null,
            Cover: null,
            Source: "spotify",
            SpotifyId: $"https://open.spotify.com/track/{spotifyTrackId}?si=abc123"));
        Assert.Equal(HttpStatusCode.Conflict, duplicateResp.StatusCode);
        Assert.Equal("application/problem+json", duplicateResp.Content.Headers.ContentType?.MediaType);
        var problem = await duplicateResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(firstDto.SongId.ToString(), problem!.Extensions["existingSongId"]?.ToString());
    }

    [Fact]
    public async Task VotingTargetEndpoint_ReflectsScheduleStatesAndDstAwareCutoff()
    {
        using var factory = new TestWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            db.Breaks.Add(new Break
            {
                Id = 1,
                BreakNumber = 1,
                BreakDate = new DateOnly(2026, 3, 16),
                StartAt = new DateTime(2026, 3, 16, 10, 0, 0),
                EndsAt = new DateTime(2026, 3, 16, 10, 15, 0),
                VoteEnds = new DateTime(2026, 3, 16, 9, 58, 0),
                MusicEnabled = true
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();

        // 1. Lekcja przed przerwą (poniedziałek 09:30 CET) -> głosowanie otwarte
        factory.TimeProviderInstance.SetLocalTime(new DateTime(2026, 3, 16, 9, 30, 0));
        var openResp = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.NotNull(openResp);
        Assert.True(openResp.IsOpen);
        Assert.Equal("open", openResp.Status);
        Assert.Equal("voting_open", openResp.ReasonCode);
        Assert.Equal(1, openResp.EligibleBreakId);
        Assert.Equal("Europe/Warsaw", openResp.TimeZone);
        Assert.Equal(TimeSpan.FromHours(1), openResp.CutoffAt!.Value.Offset);

        // 2. W trakcie przerwy po cutoff (10:05) -> zamknięte (voting_cutoff_passed)
        factory.TimeProviderInstance.SetLocalTime(new DateTime(2026, 3, 16, 10, 5, 0));
        var breakClosed = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.False(breakClosed!.IsOpen);
        Assert.Null(breakClosed.EligibleBreakId);
        Assert.Equal("closed", breakClosed.Status);
        Assert.Equal("voting_cutoff_passed", breakClosed.ReasonCode);

        // 3. Po lekcjach (18:00) -> after_hours
        factory.TimeProviderInstance.SetLocalTime(new DateTime(2026, 3, 16, 18, 0, 0));
        var afterHours = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.False(afterHours!.IsOpen);
        Assert.Equal("after_hours", afterHours.ReasonCode);

        // 4. Weekend (sobota 2026-03-21) -> weekend
        factory.TimeProviderInstance.SetLocalTime(new DateTime(2026, 3, 21, 11, 0, 0));
        var weekend = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.False(weekend!.IsOpen);
        Assert.Equal("weekend", weekend.ReasonCode);

        // 5. Przerwa wyciszona przez ExceptionBreak -> all_breaks_muted / break_muted
        factory.TimeProviderInstance.SetLocalTime(new DateTime(2026, 3, 16, 9, 30, 0));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            db.ExceptionBreaks.Add(new ExceptionBreak
            {
                BreakDate = new DateOnly(2026, 3, 16),
                BreakNumber = 1,
                MusicEnabled = false,
                Description = "Matura próbna"
            });
            await db.SaveChangesAsync();
        }

        var mutedAll = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.False(mutedAll!.IsOpen);
        Assert.Equal("all_breaks_muted", mutedAll.ReasonCode);

        var mutedExplicit = await (await client.GetAsync("/api/queue/voting-target?breakId=1")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.False(mutedExplicit!.IsOpen);
        Assert.Equal("break_muted", mutedExplicit.ReasonCode);
        Assert.Equal("Matura próbna", mutedExplicit.Reason);

        // 6. Dzień wolny ExceptionDay (day_off) -> holiday; dzień bez muzyki -> no_music_day
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            db.ExceptionDays.Add(new ExceptionDay
            {
                StartDate = new DateOnly(2026, 3, 16),
                EndDate = new DateOnly(2026, 3, 16),
                DayType = "day_off",
                Description = "Dzień Edukacji"
            });
            await db.SaveChangesAsync();
        }

        var holiday = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.False(holiday!.IsOpen);
        Assert.Equal("holiday", holiday.ReasonCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var ex = await db.ExceptionDays.SingleAsync();
            ex.DayType = "no_music";
            ex.Description = "Akademia szkolna";
            await db.SaveChangesAsync();
        }

        var noMusic = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.False(noMusic!.IsOpen);
        Assert.Equal("no_music_day", noMusic.ReasonCode);

        // 7. Sprawdzenie offsetu czasu letniego CEST (+02:00)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            db.ExceptionDays.RemoveRange(db.ExceptionDays);
            db.ExceptionBreaks.RemoveRange(db.ExceptionBreaks);
            db.Breaks.Add(new Break
            {
                Id = 2,
                BreakNumber = 1,
                BreakDate = new DateOnly(2026, 6, 15),
                StartAt = new DateTime(2026, 6, 15, 10, 0, 0),
                EndsAt = new DateTime(2026, 6, 15, 10, 15, 0),
                VoteEnds = new DateTime(2026, 6, 15, 9, 58, 0),
                MusicEnabled = true
            });
            await db.SaveChangesAsync();
        }

        factory.TimeProviderInstance.SetLocalTime(new DateTime(2026, 6, 15, 9, 0, 0));
        var summer = await (await client.GetAsync("/api/queue/voting-target")).Content.ReadFromJsonAsync<VotingTargetDto>();
        Assert.True(summer!.IsOpen);
        Assert.Equal(2, summer.EligibleBreakId);
        Assert.Equal(TimeSpan.FromHours(2), summer.CutoffAt!.Value.Offset);
    }

    [Fact]
    public async Task QueueEndpoint_IncludesUserVoteStateVotingStatusAndSpotifyMetadata()
    {
        using var factory = new TestWebApplicationFactory();
        var voter = await factory.SeedUserAsync("voter1", "VoterPass123!", rankId: 1);
        var moderator = await factory.SeedUserAsync("mod1", "ModPass123!", rankId: 2);

        // Dopasowujemy przerwę do bieżącego zegara UTC/Europe/Warsaw, żeby token JWT działał poprawnie
        var localNow = factory.TimeProviderInstance.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(localNow);
        var startLocal = localNow.AddHours(1);
        var endLocal = startLocal.AddMinutes(15);
        var cutoffLocal = startLocal.AddMinutes(-2);

        int queueItemId;
        int songId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var artist = new Artist { Name = "Daft Punk", Source = "spotify" };
            var modEntity = new Moderator { UserId = moderator.Id };
            var brk = new Break
            {
                Id = 1,
                BreakNumber = 1,
                BreakDate = today,
                StartAt = startLocal,
                EndsAt = endLocal,
                VoteEnds = cutoffLocal,
                MusicEnabled = true
            };
            db.Artists.Add(artist);
            db.Moderators.Add(modEntity);
            db.Breaks.Add(brk);
            await db.SaveChangesAsync();

            var song = new Song
            {
                Title = "Harder, Better, Faster, Stronger",
                ArtistId = artist.Id,
                DurationMs = 224000,
                Genre = "Electronic",
                Source = "spotify",
                SpotifyId = "5W3cjX2J3tjhG8zb6u0qHn",
                SongUrl = "https://open.spotify.com/track/5W3cjX2J3tjhG8zb6u0qHn",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            songId = song.Id;

            var green = new SongGreenlist { SongId = song.Id, ArtistId = artist.Id, ModId = modEntity.Id };
            db.SongGreenlists.Add(green);
            await db.SaveChangesAsync();

            var qi = new QueueItem
            {
                BreakId = brk.Id,
                SongId = song.Id,
                UserId = voter.Id,
                GreenlistId = green.Id,
                ModeratorId = modEntity.Id,
                ModerationStatus = "approved",
                Status = QueueItemStatuses.Queued,
                OrderIndex = 1,
                CreatedAt = DateTime.UtcNow
            };
            db.QueueItems.Add(qi);
            await db.SaveChangesAsync();
            queueItemId = qi.Id;
        }

        using var anonClient = factory.CreateClient();
        using var voterClient = factory.CreateClient();
        voterClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsAsync(voterClient, voter.UserLogin, "VoterPass123!"));

        var voteResp = await voterClient.PostAsJsonAsync("/api/queue/vote", new VoteRequestDto(queueItemId));
        voteResp.EnsureSuccessStatusCode();
        var voteDto = await voteResp.Content.ReadFromJsonAsync<VoteResponseDto>();
        Assert.True(voteDto!.HasVoted);
        Assert.Equal(1, voteDto.VotesCount);

        var queueForVoter = await (await voterClient.GetAsync("/api/queue"))
            .Content.ReadFromJsonAsync<QueueForBreakResponseDto>();
        Assert.NotNull(queueForVoter);
        Assert.True(queueForVoter.IsVotingOpen);
        Assert.Equal("open", queueForVoter.VotingStatus);
        Assert.Equal("voting_open", queueForVoter.VotingReasonCode);
        Assert.Contains(queueItemId, queueForVoter.UserVotedQueueItemIds);

        var row = Assert.Single(queueForVoter.Queue);
        Assert.Equal(songId, row.SongId);
        Assert.Equal("Daft Punk", row.ArtistName);
        Assert.Equal("5W3cjX2J3tjhG8zb6u0qHn", row.SpotifyId);
        Assert.Equal("spotify", row.Source);
        Assert.Equal(QueueItemStatuses.Queued, row.Status);
        Assert.Equal("approved", row.ModerationStatus);
        Assert.True(row.HasVoted);

        var queueForAnon = await (await anonClient.GetAsync("/api/queue"))
            .Content.ReadFromJsonAsync<QueueForBreakResponseDto>();
        Assert.Empty(queueForAnon!.UserVotedQueueItemIds);
        Assert.False(Assert.Single(queueForAnon.Queue).HasVoted);
    }

    [Fact]
    public async Task VotingTargetService_ComparesSchoolInstantsIndependentlyOfOsLocalTimezone()
    {
        var dbOptions = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase($"TzIsolation_{Guid.NewGuid():N}")
            .Options;

        await using var db = new schoolmusicContext(dbOptions);
        db.Breaks.Add(new Break
        {
            Id = 1,
            BreakNumber = 1,
            BreakDate = new DateOnly(2026, 3, 16),
            StartAt = new DateTime(2026, 3, 16, 10, 0, 0),
            EndsAt = new DateTime(2026, 3, 16, 10, 15, 0),
            VoteEnds = new DateTime(2026, 3, 16, 9, 58, 0),
            MusicEnabled = true
        });
        await db.SaveChangesAsync();

        // Strefa znacząco różna od UTC i CET, aby wychwycić ewentualne użycie TimeZoneInfo.Local
        var aucklandTime = new AdjustableTimeProvider(timeZoneId: "Pacific/Auckland");
        var service = new VotingTargetService(db, timeProvider: aucklandTime);

        aucklandTime.SetLocalTime(new DateTime(2026, 3, 16, 9, 30, 0));
        var open = await service.GetVotingTargetAsync();
        Assert.True(open.IsOpen);
        Assert.Equal("open", open.Status);
        Assert.Equal("voting_open", open.ReasonCode);
        Assert.Equal(1, open.EligibleBreakId);
        Assert.Equal("Pacific/Auckland", open.TimeZone);

        aucklandTime.SetLocalTime(new DateTime(2026, 3, 16, 10, 5, 0));
        var closed = await service.GetVotingTargetAsync();
        Assert.False(closed.IsOpen);
        Assert.Equal("voting_cutoff_passed", closed.ReasonCode);

        aucklandTime.SetLocalTime(new DateTime(2026, 3, 16, 18, 0, 0));
        var afterHours = await service.GetVotingTargetAsync();
        Assert.False(afterHours.IsOpen);
        Assert.Equal("after_hours", afterHours.ReasonCode);
    }
}
