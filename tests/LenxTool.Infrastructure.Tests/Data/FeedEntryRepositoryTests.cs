using System.Diagnostics;
using System.Text;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedEntryRepositoryTests : IDisposable
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000001";
    private const string SecondCategoryId = "10000000-0000-4000-8000-000000000002";
    private const string FeedId = "30000000-0000-4000-8000-000000000001";
    private const string SecondFeedId = "30000000-0000-4000-8000-000000000002";
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed entry repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpsertIsSearchableAndRepeatedFetchDoesNotDuplicateEntry()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry original = Entry("stable", "Original", "quantum local model", Now.AddHours(-2));
        FeedEntry updated = original with
        {
            Title = "Updated",
            Summary = "quantum local model revised",
            ContentHash = new string('b', 64),
            FetchedAt = Now
        };

        await repository.UpsertAsync(FeedId, [original], CancellationToken.None);
        await repository.UpsertAsync(FeedId, [updated], CancellationToken.None);
        FeedEntry? byId = await repository.GetByIdAsync(
            updated.Id,
            CancellationToken.None);
        FeedEntryPage page = await repository.QueryAsync(
            new FeedEntryQuery("quantum", null, null, null, null, FeedEntryReadFilter.All, 0, 20),
            CancellationToken.None);

        FeedEntry item = Assert.Single(page.Items);
        Assert.NotNull(byId);
        Assert.Equal(updated.Id, byId.Id);
        Assert.Equal(updated.Title, byId.Title);
        Assert.Equal(updated.Summary, byId.Summary);
        Assert.Equal(updated.ContentHash, byId.ContentHash);
        Assert.Equal("Updated", item.Title);
        Assert.Equal(updated.ContentHash, item.ContentHash);
        Assert.False(page.HasMore);
        await using SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand fts = connection.CreateCommand();
        fts.CommandText = "SELECT COUNT(*) FROM content_fts WHERE entity_type='feed_entry' AND entity_id=$id;";
        fts.Parameters.AddWithValue("$id", item.Id);
        Assert.Equal(1L, (long)(await fts.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task QuerySupportsStablePagingAndPrivateStateFilters()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry newest = Entry("newest", "Newest", "alpha", Now.AddDays(-1));
        FeedEntry older = Entry("older", "Older", "beta", Now.AddDays(-2));
        FeedEntry otherFeed = Entry(
            "other",
            "Other feed",
            "gamma",
            Now.AddDays(-3),
            SecondFeedId);
        await repository.UpsertAsync(FeedId, [older, newest], CancellationToken.None);
        await repository.UpsertAsync(SecondFeedId, [otherFeed], CancellationToken.None);
        const string tagId = "50000000-0000-4000-8000-000000000001";
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand privateState = connection.CreateCommand())
        {
            privateState.CommandText = """
                INSERT INTO user_entry_states(
                    entry_id, local_profile, is_read, is_starred, progress, note, updated_at)
                VALUES($readId, 'default', 1, 0, 0, '', $now),
                      ($stateFavoriteId, 'default', 0, 1, 0, '', $now),
                      ($repositoryFavoriteId, 'secondary', 1, 0, 0, '', $now);
                INSERT INTO favorites(id, entity_type, entity_id, note, created_at)
                VALUES('favorite-filter', 'feed_entry', $repositoryFavoriteId, '', $now);
                INSERT INTO tags(id, name, color, created_at)
                VALUES($tagId, '精读', '#4B6B88', $now);
                INSERT INTO entity_tags(entity_type, entity_id, tag_id)
                VALUES('feed_entry', $stateFavoriteId, $tagId);
                """;
            privateState.Parameters.AddWithValue("$readId", newest.Id);
            privateState.Parameters.AddWithValue("$stateFavoriteId", older.Id);
            privateState.Parameters.AddWithValue("$repositoryFavoriteId", otherFeed.Id);
            privateState.Parameters.AddWithValue("$tagId", tagId);
            privateState.Parameters.AddWithValue("$now", Now.ToString("O"));
            await privateState.ExecuteNonQueryAsync(CancellationToken.None);
        }

        FeedEntryPage first = await repository.QueryAsync(
            Query(feedId: FeedId, offset: 0, limit: 1),
            CancellationToken.None);
        FeedEntryPage second = await repository.QueryAsync(
            Query(feedId: FeedId, offset: 1, limit: 1),
            CancellationToken.None);
        FeedEntryPage category = await repository.QueryAsync(
            Query(categoryId: SecondCategoryId),
            CancellationToken.None);
        FeedEntryPage date = await repository.QueryAsync(
            Query(publishedFrom: Now.AddDays(-2), publishedBefore: Now),
            CancellationToken.None);
        FeedEntryPage unread = await repository.QueryAsync(
            Query(readFilter: FeedEntryReadFilter.Unread),
            CancellationToken.None);
        FeedEntryPage read = await repository.QueryAsync(
            Query(readFilter: FeedEntryReadFilter.Read),
            CancellationToken.None);
        FeedEntryPage secondaryRead = await repository.QueryAsync(
            Query(
                readFilter: FeedEntryReadFilter.Read,
                localProfile: "secondary"),
            CancellationToken.None);
        FeedEntryPage favorites = await repository.QueryAsync(
            Query(favoritesOnly: true),
            CancellationToken.None);
        FeedEntryPage tagged = await repository.QueryAsync(
            Query(tagId: tagId),
            CancellationToken.None);

        Assert.Equal(["newest"], first.Items.Select(item => item.ExternalId));
        Assert.True(first.HasMore);
        Assert.Equal(["older"], second.Items.Select(item => item.ExternalId));
        Assert.False(second.HasMore);
        Assert.Equal(["other"], category.Items.Select(item => item.ExternalId));
        Assert.Equal(["newest", "older"], date.Items.Select(item => item.ExternalId));
        Assert.Equal(["older", "other"], unread.Items.Select(item => item.ExternalId).Order().ToArray());
        Assert.Equal(["newest"], read.Items.Select(item => item.ExternalId));
        Assert.Equal(["other"], secondaryRead.Items.Select(item => item.ExternalId));
        Assert.Equal(
            ["older", "other"],
            favorites.Items.Select(item => item.ExternalId).Order().ToArray());
        Assert.Equal(["older"], tagged.Items.Select(item => item.ExternalId));
    }

    [Fact]
    public async Task ViewKindQueryFiltersInsidePagingAndReturnsRawContinuation()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry article = Entry("article", "Article", "article", Now);
        FeedEntry newestPicture = Entry(
            "picture-new",
            "Newest picture",
            "picture",
            Now.AddMinutes(-1)) with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example/picture-new.jpg",
                    "image/jpeg",
                    128,
                    "Newest picture")
            ]
        };
        FeedEntry audio = Entry(
            "audio",
            "Audio",
            "audio",
            Now.AddMinutes(-2)) with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example/audio.mp3",
                    "audio/mpeg",
                    256,
                    "Audio")
            ]
        };
        FeedEntry olderPicture = Entry(
            "picture-old",
            "Older picture",
            "picture",
            Now.AddMinutes(-3)) with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example/picture-old.png",
                    "image/png",
                    512,
                    "Older picture")
            ]
        };
        FeedEntry oldestArticle = Entry(
            "article-old",
            "Old article",
            "article",
            Now.AddMinutes(-4));
        await repository.UpsertAsync(
            FeedId,
            [article, newestPicture, audio, olderPicture, oldestArticle],
            CancellationToken.None);

        FeedEntryPage first = await repository.QueryAsync(
            Query(limit: 1, viewKind: EntryViewKind.Picture),
            CancellationToken.None);
        FeedEntryPage second = await repository.QueryAsync(
            Query(
                offset: Assert.IsType<int>(first.NextOffset),
                limit: 1,
                viewKind: EntryViewKind.Picture),
            CancellationToken.None);

        Assert.Equal(["picture-new"], first.Items.Select(item => item.ExternalId));
        Assert.True(first.HasMore);
        Assert.Equal(3, first.NextOffset);
        Assert.Equal(["picture-old"], second.Items.Select(item => item.ExternalId));
        Assert.False(second.HasMore);
        Assert.Equal(5, second.NextOffset);
    }

    [Fact]
    public async Task NonArticleCatalogViewActsAsExplicitEntryViewOverride()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(2, FeedCatalogScope.Active, Now, Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 2, Now, Now),
                new(SecondCategoryId, "Science", "science", 2, true, 2, Now, Now)
            ],
            [
                CatalogFeed(FeedId, CategoryId, 1),
                CatalogFeed(SecondFeedId, SecondCategoryId, 2) with
                {
                    ViewKind = FeedViewKind.Picture,
                    IsViewKindExplicit = true,
                    Version = 2
                }
            ]), CancellationToken.None);
        FeedEntry overridden = Entry(
            "catalog-picture",
            "Catalog picture",
            "No enclosure is required for an explicit picture feed.",
            Now,
            SecondFeedId);
        await repository.UpsertAsync(
            SecondFeedId,
            [overridden],
            CancellationToken.None);

        FeedEntryPage page = await repository.QueryAsync(
            Query(viewKind: EntryViewKind.Picture),
            CancellationToken.None);

        Assert.Equal(["catalog-picture"], page.Items.Select(item => item.ExternalId));
    }

    [Fact]
    public async Task ExplicitArticleOverridePreventsMediaPromotion()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(2, FeedCatalogScope.Active, Now, Now),
            [new(CategoryId, "Technology", "technology", 1, true, 2, Now, Now)],
            [CatalogFeed(FeedId, CategoryId, 2) with
            {
                ViewKind = FeedViewKind.Article,
                IsViewKindExplicit = true
            }]), CancellationToken.None);
        FeedEntry mediaEntry = Entry("forced-article", "Forced article", "article", Now) with
        {
            Enclosures = [new("https://cdn.example/forced.jpg", "image/jpeg", 128, "Image")]
        };
        await repository.UpsertAsync(FeedId, [mediaEntry], CancellationToken.None);

        FeedEntryPage articles = await repository.QueryAsync(
            Query(viewKind: EntryViewKind.Article),
            CancellationToken.None);
        FeedEntryPage pictures = await repository.QueryAsync(
            Query(viewKind: EntryViewKind.Picture),
            CancellationToken.None);

        Assert.Contains(articles.Items, item => item.ExternalId == "forced-article");
        Assert.DoesNotContain(pictures.Items, item => item.ExternalId == "forced-article");
    }

    [Fact]
    public async Task ViewKindPagingScansOneThousandMixedEntriesWithoutDuplicates()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry[] mixed = Enumerable.Range(0, 1_000)
            .Select(index => Entry(
                $"mixed-{index:D4}",
                $"Mixed {index:D4}",
                "mixed",
                Now.AddMinutes(-index)) with
            {
                Enclosures = index % 4 == 0
                    ?
                    [
                        new(
                            $"https://cdn.example/mixed-{index:D4}.jpg",
                            "image/jpeg",
                            128,
                            $"Picture {index:D4}")
                    ]
                    : []
            })
            .ToArray();
        await repository.UpsertAsync(FeedId, mixed, CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var ids = new List<string>();
        int offset = 0;
        while (true)
        {
            FeedEntryPage page = await repository.QueryAsync(
                Query(
                    offset: offset,
                    limit: 50,
                    viewKind: EntryViewKind.Picture),
                CancellationToken.None);
            ids.AddRange(page.Items.Select(item => item.Id));
            if (!page.HasMore) break;
            offset = Assert.IsType<int>(page.NextOffset);
        }
        stopwatch.Stop();

        Assert.Equal(250, ids.Count);
        Assert.Equal(250, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Mixed view paging took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RetentionDeletesOnlyOldEntriesWithoutPrivateStateAndCleansFts()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry expired = Entry("expired", "Expired", "remove-marker", Now.AddDays(-200));
        FeedEntry favorite = Entry("favorite", "Favorite", "favorite-marker", Now.AddDays(-210));
        FeedEntry tagged = Entry("tagged", "Tagged", "tagged-marker", Now.AddDays(-220));
        FeedEntry stateful = Entry("stateful", "Stateful", "stateful-marker", Now.AddDays(-230));
        FeedEntry fullTextActive = Entry(
            "full-text-active",
            "Full text active",
            "full-text-active-marker",
            Now.AddDays(-240));
        FeedEntry aiActive = Entry(
            "ai-active",
            "AI active",
            "ai-active-marker",
            Now.AddDays(-250));
        FeedEntry ruleActive = Entry(
            "rule-active",
            "Rule active",
            "rule-active-marker",
            Now.AddDays(-260));
        FeedEntry mediaActive = Entry(
            "media-active",
            "Media active",
            "media-active-marker",
            Now.AddDays(-270));
        FeedEntry recent = Entry("recent", "Recent", "recent-marker", Now.AddDays(-20));
        await repository.UpsertAsync(
            FeedId,
            [
                expired,
                favorite,
                tagged,
                stateful,
                fullTextActive,
                aiActive,
                ruleActive,
                mediaActive,
                recent
            ],
            CancellationToken.None);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand state = connection.CreateCommand())
        {
            state.CommandText = """
                INSERT INTO favorites(id, entity_type, entity_id, note, created_at)
                VALUES('favorite-state', 'feed_entry', $favoriteId, 'keep', $now);
                INSERT INTO tags(id, name, color, created_at)
                VALUES('tag-state', 'keep', 'neutral', $now);
                INSERT INTO entity_tags(entity_type, entity_id, tag_id)
                VALUES('feed_entry', $taggedId, 'tag-state');
                INSERT INTO user_entry_states(
                    entry_id, local_profile, is_read, is_starred, progress, note, updated_at)
                VALUES($statefulId, 'default', 1, 0, 25, 'keep', $now);

                INSERT INTO feed_full_text_jobs(
                    entry_id, host, status, attempt_count, next_attempt_at,
                    lease_expires_at, lease_id, last_error_code, updated_at)
                VALUES(
                    $fullTextActiveId, 'feeds.example', 'PENDING', 0, $now,
                    NULL, NULL, NULL, $now);

                INSERT INTO feed_ai_automation_jobs(
                    id, feed_id, entry_id, content_hash, task_type,
                    target_language, status, attempt_count, next_attempt_at,
                    lease_token, lease_expires_at, last_error_code,
                    created_at, updated_at)
                VALUES(
                    '60000000-0000-4000-8000-000000000001',
                    $feedId, $aiActiveId, $contentHash, 'SUMMARY',
                    'zh-CN', 'PENDING', 0, $now,
                    NULL, NULL, NULL, $now, $now);

                INSERT INTO feed_automation_runs(
                    entry_id, rule_id, rule_version, evaluation_outcome,
                    plan_order, evaluated_at)
                VALUES(
                    $ruleActiveId,
                    '70000000-0000-4000-8000-000000000001',
                    1, 'MATCHED', 0, $now);
                INSERT INTO feed_automation_action_runs(
                    idempotency_key, entry_id, rule_id, rule_version,
                    rule_priority, rule_conflict_order, action_type,
                    action_order, action_value, disposition,
                    suppression_reason, winning_rule_id,
                    winning_rule_version, winning_action_order, status,
                    attempt_count, next_attempt_at, lease_token,
                    lease_expires_at, last_error_code, created_at, updated_at)
                VALUES(
                    $idempotencyKey, $ruleActiveId,
                    '70000000-0000-4000-8000-000000000001',
                    1, 100, 0, 'HIDE', 0, NULL, 'PLANNED',
                    'NONE', NULL, NULL, NULL, 'PENDING',
                    0, $now, NULL, NULL, NULL, $now, $now);

                INSERT INTO media_jobs(
                    id, kind, input_path, output_path, status, progress,
                    engine, model, shared_usage_seconds, ai_request_count,
                    error_json, created_at, updated_at)
                VALUES(
                    '80000000-0000-4000-8000-000000000001',
                    'FeedTranscription', 'C:\media\active.mp3', NULL,
                    'Running', 10, 'Groq', NULL, 0, 0, NULL, $now, $now);
                INSERT INTO feed_media_deliveries(
                    entry_id, feed_id, entry_title, source_url,
                    source_title, media_type, source_length, media_job_id,
                    created_at)
                VALUES(
                    $mediaActiveId, $feedId, 'Media active',
                    'https://cdn.example/media-active.mp3',
                    'Media active', 'audio/mpeg', 128,
                    '80000000-0000-4000-8000-000000000001', $now);
                """;
            state.Parameters.AddWithValue("$favoriteId", favorite.Id);
            state.Parameters.AddWithValue("$taggedId", tagged.Id);
            state.Parameters.AddWithValue("$statefulId", stateful.Id);
            state.Parameters.AddWithValue(
                "$fullTextActiveId",
                fullTextActive.Id);
            state.Parameters.AddWithValue("$aiActiveId", aiActive.Id);
            state.Parameters.AddWithValue("$ruleActiveId", ruleActive.Id);
            state.Parameters.AddWithValue("$mediaActiveId", mediaActive.Id);
            state.Parameters.AddWithValue("$feedId", FeedId);
            state.Parameters.AddWithValue("$contentHash", new string('f', 64));
            state.Parameters.AddWithValue("$idempotencyKey", new string('a', 64));
            state.Parameters.AddWithValue("$now", Now.ToString("O"));
            await state.ExecuteNonQueryAsync(CancellationToken.None);
        }

        int deleted = await repository.DeleteExpiredUnprotectedAsync(
            Now.AddDays(-180),
            100,
            CancellationToken.None);

        Assert.Equal(1, deleted);
        FeedEntryPage remaining = await repository.QueryAsync(Query(), CancellationToken.None);
        Assert.Equal(
            [
                "ai-active",
                "favorite",
                "full-text-active",
                "media-active",
                "recent",
                "rule-active",
                "stateful",
                "tagged"
            ],
            remaining.Items.Select(item => item.ExternalId).Order().ToArray());
        Assert.Empty((await repository.QueryAsync(
            Query(searchText: "remove-marker"),
            CancellationToken.None)).Items);
        Assert.Single((await repository.QueryAsync(
            Query(searchText: "favorite-marker"),
            CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ActiveEntryExportProtectsSourceFromRetentionUntilTaskIsTerminal()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = new(_testRoot);
        var entries = new FeedEntryRepository(database);
        var exports = new EntryExportTaskRepository(database);
        using var assets = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 1024, MaximumAssetBytes: 128));
        var maintenance = new DatabaseMaintenanceService(
            paths,
            database,
            entries,
            assets);
        DateTimeOffset cutoff = Now.AddDays(-180);
        FeedEntry expired = Entry(
            "active-export",
            "Active export",
            "active-export-marker",
            Now.AddDays(-200));
        await entries.UpsertAsync(
            FeedId,
            [expired],
            CancellationToken.None);
        EntryExportRequest request = EntryExportRequest.Create(
            "markdown",
            "retention-target",
            expired,
            EntryViewKind.Article,
            128);
        await exports.EnqueueAsync(
            request,
            Now,
            CancellationToken.None);

        StorageCleanupPreview queuedPreview =
            await maintenance.PreviewCleanupAsync(
                cutoff,
                CancellationToken.None);
        Assert.Equal(0, queuedPreview.ExpiredFeedEntryCount);
        Assert.Equal(0, await entries.DeleteExpiredUnprotectedAsync(
            cutoff,
            100,
            CancellationToken.None));
        Assert.NotNull(await entries.GetByIdAsync(
            expired.Id,
            CancellationToken.None));

        EntryExportTaskLease lease = Assert.IsType<EntryExportTaskLease>(
            await exports.ClaimDueAsync(
                Now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        StorageCleanupPreview runningPreview =
            await maintenance.PreviewCleanupAsync(
                cutoff,
                CancellationToken.None);
        Assert.Equal(0, runningPreview.ExpiredFeedEntryCount);
        Assert.Equal(0, await entries.DeleteExpiredUnprotectedAsync(
            cutoff,
            100,
            CancellationToken.None));
        Assert.NotNull(await entries.GetByIdAsync(
            expired.Id,
            CancellationToken.None));

        await exports.CompleteAsync(
            lease,
            Now.AddMinutes(1),
            CancellationToken.None);
        StorageCleanupPreview completedPreview =
            await maintenance.PreviewCleanupAsync(
                cutoff,
                CancellationToken.None);
        Assert.Equal(1, completedPreview.ExpiredFeedEntryCount);
        Assert.Equal(1, await entries.DeleteExpiredUnprotectedAsync(
            cutoff,
            100,
            CancellationToken.None));
        Assert.Null(await entries.GetByIdAsync(
            expired.Id,
            CancellationToken.None));
        EntryExportTask history = Assert.IsType<EntryExportTask>(
            await exports.GetAsync(
                request.IdempotencyKey,
                CancellationToken.None));
        Assert.Equal(EntryExportTaskStatus.Completed, history.Status);
        Assert.Equal(expired.Id, history.EntryId);
    }

    [Fact]
    public async Task UnifiedContentSearchReturnsFeedEntryWithCatalogSource()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry entry = Entry(
            "unified",
            "Unified search entry",
            "rare-unified-marker",
            Now.AddHours(-1));
        await repository.UpsertAsync(FeedId, [entry], CancellationToken.None);

        IReadOnlyList<ContentSearchResult> results = await new NewsRepository(database)
            .SearchContentAsync("rare-unified", 20, CancellationToken.None);

        ContentSearchResult result = Assert.Single(results);
        Assert.Equal(ContentSearchResultType.FeedEntry, result.Type);
        Assert.Equal("Daily Feed", result.Source);
        Assert.Equal(entry.NormalizedUrl, result.Url);
        Assert.Equal("订阅条目", result.TypeLabel);
    }

    [Fact]
    public async Task ActiveOnlyQueryExcludesDisabledFeedsAndCategories()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry active = Entry("active", "Active", "active-marker", Now.AddHours(-1), FeedId);
        FeedEntry disabled = Entry("disabled", "Disabled", "disabled-marker", Now.AddHours(-2), SecondFeedId);
        await repository.UpsertAsync(FeedId, [active], CancellationToken.None);
        await repository.UpsertAsync(SecondFeedId, [disabled], CancellationToken.None);

        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(2, FeedCatalogScope.Active, Now, Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 2, Now.AddDays(-1), Now),
                new(SecondCategoryId, "Science", "science", 2, false, 2, Now.AddDays(-1), Now)
            ],
            [
                CatalogFeed(FeedId, CategoryId, 1),
                CatalogFeed(SecondFeedId, SecondCategoryId, 2) with { IsEnabled = true }
            ]), CancellationToken.None);

        FeedEntryPage page = await repository.QueryAsync(
            Query(activeOnly: true),
            CancellationToken.None);

        Assert.Equal(["active"], page.Items.Select(item => item.ExternalId));

        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(3, FeedCatalogScope.Active, Now, Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 3, Now.AddDays(-1), Now),
                new(SecondCategoryId, "Science", "science", 2, true, 3, Now.AddDays(-1), Now)
            ],
            [
                CatalogFeed(FeedId, CategoryId, 1),
                CatalogFeed(SecondFeedId, SecondCategoryId, 2) with { IsEnabled = false }
            ]), CancellationToken.None);

        page = await repository.QueryAsync(Query(activeOnly: true), CancellationToken.None);

        Assert.Equal(["active"], page.Items.Select(item => item.ExternalId));
    }

    [Fact]
    public async Task QueryExcludesHiddenEntriesOnlyForTheSelectedProfile()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry visible = Entry("visible", "Visible", "visible", Now.AddHours(-1));
        FeedEntry hidden = Entry("hidden", "Hidden", "hidden", Now.AddHours(-2));
        await repository.UpsertAsync(
            FeedId,
            [visible, hidden],
            CancellationToken.None);
        await new EntryStateRepository(database).PatchAsync(
            hidden.Id,
            "default",
            new(IsHidden: true),
            CancellationToken.None);

        FeedEntryPage defaultPage = await repository.QueryAsync(
            Query(),
            CancellationToken.None);
        FeedEntryPage includingHidden = await repository.QueryAsync(
            Query(includeHidden: true),
            CancellationToken.None);
        FeedEntryPage otherProfile = await repository.QueryAsync(
            Query(localProfile: "secondary"),
            CancellationToken.None);

        Assert.Equal(["visible"], defaultPage.Items.Select(item => item.ExternalId));
        Assert.Equal(
            ["visible", "hidden"],
            includingHidden.Items.Select(item => item.ExternalId));
        Assert.Equal(
            ["visible", "hidden"],
            otherProfile.Items.Select(item => item.ExternalId));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var catalog = new FeedCatalogRepository(database);
        await catalog.ReplaceAsync(new(
            new(1, FeedCatalogScope.Active, Now.AddHours(-1), Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 1, Now.AddDays(-1), Now),
                new(SecondCategoryId, "Science", "science", 2, true, 1, Now.AddDays(-1), Now)
            ],
            [
                CatalogFeed(FeedId, CategoryId, 1),
                CatalogFeed(SecondFeedId, SecondCategoryId, 2)
            ]), CancellationToken.None);
        return database;
    }

    private static FeedCatalogItem CatalogFeed(string feedId, string categoryId, int sortOrder) => new(
        feedId,
        $"https://feeds.example/{feedId}.xml",
        $"https://feeds.example/{feedId}.xml",
        feedId == FeedId ? "Daily Feed" : "Second Feed",
        "https://feeds.example/",
        categoryId,
        FeedViewKind.Article,
        60,
        sortOrder,
        true,
        1,
        Now.AddDays(-1),
        Now);

    private static FeedEntry Entry(
        string externalId,
        string title,
        string content,
        DateTimeOffset publishedAt,
        string feedId = FeedId)
    {
        string xml = $"<rss version='2.0'><channel><title>x</title><item><guid>{externalId}</guid><title>{title}</title><pubDate>{publishedAt:R}</pubDate><description>{content}</description></item></channel></rss>";
        return Assert.Single(new FeedDocumentParser().Parse(
            feedId,
            $"https://feeds.example/{feedId}.xml",
            Encoding.UTF8.GetBytes(xml),
            Now).Entries);
    }

    private static FeedEntryQuery Query(
        string? searchText = null,
        string? feedId = null,
        string? categoryId = null,
        DateTimeOffset? publishedFrom = null,
        DateTimeOffset? publishedBefore = null,
        FeedEntryReadFilter readFilter = FeedEntryReadFilter.All,
        int offset = 0,
        int limit = 20,
        bool activeOnly = false,
        bool favoritesOnly = false,
        string? tagId = null,
        string localProfile = "default",
        bool includeHidden = false,
        EntryViewKind? viewKind = null) => new(
            searchText,
            feedId,
            categoryId,
            publishedFrom,
            publishedBefore,
            readFilter,
            offset,
            limit,
            activeOnly,
            favoritesOnly,
            tagId,
            localProfile,
            includeHidden,
            viewKind);
}
