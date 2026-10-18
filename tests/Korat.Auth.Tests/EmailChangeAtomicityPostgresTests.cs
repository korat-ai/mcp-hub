using System.Collections.Concurrent;
using System.Data.Common;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Korat.Auth.Tests;

/// <summary>
/// Real PostgreSQL regressions. KORAT_TEST_POSTGRES may name an owned, disposable
/// local test server; otherwise Testcontainers supplies PostgreSQL 16 (skip if unavailable).
/// Each case creates/drops only its own unique database. The email sender alone is fake.
/// </summary>
public sealed class EmailChangeAtomicityPostgresTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);
    private const string OldEmail = "owner@example.com";
    private const string NewEmail = "replacement@example.com";

    [SkippableFact]
    public Task ConfirmAsync_CancelledAfterUserUpdate_RollsBackEmailAndToken_WithoutAlert() =>
        WithFreshDatabaseAsync(async (connectionString, deadline) =>
        {
            var seed = await SeedAsync(connectionString, deadline);
            var sender = new RecordingSender();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            var interceptor = new AfterUserUpdateInterceptor(ct =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The request cancellation did not reach the UPDATE.");
            });

            await using (var db = CreateContext(connectionString, interceptor))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    CreateService(db, sender).ConfirmAsync(seed.UserId, seed.RawToken, cancellation.Token));
            }

            // Observe persisted rows through a NEW context, never the stale ExecuteUpdate tracker.
            Assert.Equal(1, interceptor.ExecutedUpdates);
            await AssertStateAsync(connectionString, seed, consumed: false, deadline);
            Assert.Empty(sender.Alerts);
        });

    [SkippableFact]
    public Task ConfirmAsync_ExceptionAfterUserUpdate_RollsBackEmailAndToken_WithoutAlert() =>
        WithFreshDatabaseAsync(async (connectionString, deadline) =>
        {
            var seed = await SeedAsync(connectionString, deadline);
            var sender = new RecordingSender();
            var injected = new ApplicationException("Injected after PostgreSQL updated the user.");
            var interceptor = new AfterUserUpdateInterceptor(_ => throw injected);

            await using (var db = CreateContext(connectionString, interceptor))
            {
                var actual = await Assert.ThrowsAsync<ApplicationException>(() =>
                    CreateService(db, sender).ConfirmAsync(seed.UserId, seed.RawToken, deadline));
                Assert.Same(injected, actual);
            }

            Assert.Equal(1, interceptor.ExecutedUpdates);
            await AssertStateAsync(connectionString, seed, consumed: false, deadline);
            Assert.Empty(sender.Alerts);

            // A rollback must leave the claim usable, not merely hide an email change.
            var retry = await ConfirmInNewContextAsync(connectionString, seed, sender, deadline);
            Assert.Equal(EmailChangeConfirmStatus.Success, retry.Status);
            await AssertStateAsync(connectionString, seed, consumed: true, deadline);
            Assert.Equal((OldEmail, NewEmail), Assert.Single(sender.Alerts));
        });

    [SkippableFact]
    public Task ConfirmAsync_SameTokenConcurrentRequests_OnlyOneSucceedsAndAlerts() =>
        WithFreshDatabaseAsync(async (connectionString, deadline) =>
        {
            var seed = await SeedAsync(connectionString, deadline);
            var sender = new RecordingSender();
            var gate = new TwoRequestGate();
            using var requests = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            var first = new BeforeFirstUpdateInterceptor(gate.ArriveAsync);
            var second = new BeforeFirstUpdateInterceptor(gate.ArriveAsync);

            // Each context has its own first-UPDATE interceptor. With the fixed service
            // this is the conditional token UPDATE, BEFORE either request holds its row
            // lock; with the old service it is the user UPDATE. Both eligibility reads
            // therefore complete before either request is allowed to write.
            var both = Task.WhenAll(
                ConfirmInNewContextAsync(connectionString, seed, sender, requests.Token, first),
                ConfirmInNewContextAsync(connectionString, seed, sender, requests.Token, second));
            EmailChangeConfirmResult[] results;
            try
            {
                results = await both.WaitAsync(TimeSpan.FromSeconds(45), deadline);
            }
            finally
            {
                requests.Cancel();
                gate.Release();
                // Do not dispose contexts/drop the database while a losing request is
                // still running. Preserve the primary failure after draining both tasks.
                if (!both.IsCompleted)
                {
                    try
                    {
                        await both.WaitAsync(WaitLimit);
                    }
                    catch (Exception) when (both.IsCompleted)
                    {
                        // The original await already reports the request failure. A drain
                        // timeout with requests STILL running must not be swallowed.
                    }
                }
            }

            Assert.Equal(2, gate.Arrivals);
            Assert.Equal(1, first.Entries);
            Assert.Equal(1, second.Entries);
            var success = Assert.Single(results, r => r.Status == EmailChangeConfirmStatus.Success);
            Assert.Equal(NewEmail, success.NewEmail);
            var rejected = Assert.Single(results, r => r.Status == EmailChangeConfirmStatus.ExpiredOrInvalid);
            Assert.Null(rejected.NewEmail);
            Assert.Equal((OldEmail, NewEmail), Assert.Single(sender.Alerts));
            await AssertStateAsync(connectionString, seed, consumed: true, deadline);
            await AssertReplayInvalidAsync(connectionString, seed, sender, deadline);
        });

    [SkippableFact]
    public Task ConfirmAsync_TargetEmailTakenBeforeUserUpdate_ReturnsInvalidAndRollsBackClaim() =>
        WithFreshDatabaseAsync(async (connectionString, deadline) =>
        {
            var seed = await SeedAsync(connectionString, deadline);
            var sender = new RecordingSender();
            var competitorId = UserId.New();
            var interceptor = new BeforeFirstUpdateInterceptor(async ct =>
            {
                // Commit the competing owner AFTER confirmation read an eligible token,
                // immediately BEFORE its actual email UPDATE hits the unique constraint.
                await using var competitor = CreateContext(connectionString);
                competitor.Users.Add(NewUser(competitorId, NewEmail));
                await competitor.SaveChangesAsync(ct);
            }, userOnly: true);

            var result = await ConfirmInNewContextAsync(connectionString, seed, sender, deadline, interceptor);

            Assert.Equal(1, interceptor.Entries);
            Assert.Equal(1, Assert.Single(interceptor.TokenUpdateResults));
            Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, result.Status);
            Assert.Null(result.NewEmail);
            await AssertStateAsync(connectionString, seed, consumed: false, deadline);
            Assert.Empty(sender.Alerts);
            await using var observation = CreateContext(connectionString);
            Assert.Equal(NewEmail, (await observation.Users.AsNoTracking()
                .SingleAsync(u => u.Id == competitorId, deadline)).PrimaryEmail);
        });

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConfirmAsync_CommitsBeforeAlert_AndReplayIsInvalid_EvenIfSenderFails(bool senderFails) =>
        WithFreshDatabaseAsync(async (connectionString, deadline) =>
        {
            var seed = await SeedAsync(connectionString, deadline);
            Task? alertObservation = null;
            var sender = new RecordingSender(
                ct => alertObservation = AssertStateAsync(connectionString, seed, consumed: true, ct),
                senderFails);

            // Check tracking explicitly: replay alone could pass via the SQL predicate
            // while the tracked email/token remained stale. Unrelated pending changes
            // must neither be flushed by confirmation nor discarded by synchronization.
            await using (var db = CreateContext(connectionString))
            {
                var trackedUser = await db.Users.SingleAsync(u => u.Id == seed.UserId, deadline);
                var displayName = db.Entry(trackedUser).Property(u => u.DisplayName);
                var originalDisplayName = displayName.OriginalValue;
                displayName.CurrentValue = "Pending profile edit";

                var service = CreateService(db, sender);
                var result = await service.ConfirmAsync(seed.UserId, seed.RawToken, deadline);
                Assert.Equal(EmailChangeConfirmStatus.Success, result.Status);
                Assert.Equal(NewEmail, result.NewEmail);
                Assert.NotNull(alertObservation);
                // Service catches sender exceptions, including assertion exceptions. Await
                // the observation AGAIN outside the sender so these assertions cannot vanish.
                await alertObservation!;
                Assert.Equal(senderFails ? 1 : 0, sender.InjectedFailures);
                Assert.Equal((OldEmail, NewEmail), Assert.Single(sender.Alerts));

                var email = db.Entry(trackedUser).Property(u => u.PrimaryEmail);
                Assert.Equal(NewEmail, email.CurrentValue);
                Assert.Equal(NewEmail, email.OriginalValue);
                Assert.False(email.IsModified);
                var trackedToken = await db.EmailChangeTokens.SingleAsync(t => t.Id == seed.TokenId, deadline);
                var consumedAt = db.Entry(trackedToken).Property(t => t.ConsumedAt);
                Assert.Equal(Now, consumedAt.CurrentValue);
                Assert.Equal(Now, consumedAt.OriginalValue);
                Assert.False(consumedAt.IsModified);
                Assert.Equal("Pending profile edit", displayName.CurrentValue);
                Assert.Equal(originalDisplayName, displayName.OriginalValue);
                Assert.True(displayName.IsModified);
                await using (var observation = CreateContext(connectionString))
                {
                    Assert.Equal(originalDisplayName, (await observation.Users.AsNoTracking()
                        .SingleAsync(u => u.Id == seed.UserId, deadline)).DisplayName);
                }

                var replay = await service.ConfirmAsync(seed.UserId, seed.RawToken, deadline);
                Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, replay.Status);
                Assert.Null(replay.NewEmail);

                await db.SaveChangesAsync(deadline);
                await using var saved = CreateContext(connectionString);
                Assert.Equal("Pending profile edit", (await saved.Users.AsNoTracking()
                    .SingleAsync(u => u.Id == seed.UserId, deadline)).DisplayName);
            }

            await AssertStateAsync(connectionString, seed, consumed: true, deadline);
            await AssertReplayInvalidAsync(connectionString, seed, sender, deadline);
        });

    [SkippableFact]
    public Task ConfirmAsync_UserChangedBeforeUpdate_ZeroAffectedRowsRollsBackTokenClaim() =>
        WithFreshDatabaseAsync(async (connectionString, deadline) =>
        {
            const string interveningEmail = "intervening@example.com";
            var seed = await SeedAsync(connectionString, deadline);
            var sender = new RecordingSender();
            var interceptor = new BeforeFirstUpdateInterceptor(async ct =>
            {
                await using var competitor = CreateContext(connectionString);
                Assert.Equal(1, await competitor.Users.Where(u => u.Id == seed.UserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.PrimaryEmail, interveningEmail), ct));
            }, userOnly: true);

            var result = await ConfirmInNewContextAsync(connectionString, seed, sender, deadline, interceptor);

            Assert.Equal(1, interceptor.Entries);
            Assert.Equal(1, Assert.Single(interceptor.TokenUpdateResults));
            Assert.Equal(0, Assert.Single(interceptor.UserUpdateResults));
            Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, result.Status);
            Assert.Null(result.NewEmail);
            await AssertStateAsync(connectionString, seed, consumed: false, deadline, interveningEmail);
            Assert.Empty(sender.Alerts);
        });

    private static async Task<EmailChangeConfirmResult> ConfirmInNewContextAsync(
        string connectionString, Seed seed, RecordingSender sender, CancellationToken ct,
        params IInterceptor[] interceptors)
    {
        await using var db = CreateContext(connectionString, interceptors);
        return await CreateService(db, sender).ConfirmAsync(seed.UserId, seed.RawToken, ct);
    }

    private static async Task AssertReplayInvalidAsync(
        string connectionString, Seed seed, RecordingSender sender, CancellationToken ct)
    {
        var replay = await ConfirmInNewContextAsync(connectionString, seed, sender, ct);
        Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, replay.Status);
        Assert.Null(replay.NewEmail);
        Assert.Equal((OldEmail, NewEmail), Assert.Single(sender.Alerts));
        await AssertStateAsync(connectionString, seed, consumed: true, ct);
    }

    private static EmailChangeService CreateService(KoratDbContext db, RecordingSender sender) =>
        new(db, sender, NullLogger<EmailChangeService>.Instance, new FixedTimeProvider());

    private static KoratDbContext CreateContext(string connectionString, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<KoratDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptors)
            .Options;
        return new KoratDbContext(options);
    }

    private static async Task<Seed> SeedAsync(string connectionString, CancellationToken ct)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync(ct);
        var seed = new Seed(UserId.New(), Guid.NewGuid(), AuthTokens.GenerateRawBase64Url());
        db.Users.Add(NewUser(seed.UserId, OldEmail));
        db.EmailChangeTokens.Add(new EmailChangeToken
        {
            Id = seed.TokenId,
            UserId = seed.UserId,
            NewEmail = NewEmail,
            TokenHash = AuthTokens.Sha256Hex(seed.RawToken),
            CreatedAt = Now,
            ExpiresAt = Now + EmailChangeService.TokenTtl,
        });
        await db.SaveChangesAsync(ct);
        return seed;
    }

    private static User NewUser(UserId id, string email) => new()
    {
        Id = id,
        PrimaryEmail = email,
        DisplayName = "Email atomicity test",
        CreatedAt = Now,
        Status = UserStatus.Active,
        IsAdmin = false,
    };

    private static async Task AssertStateAsync(
        string connectionString, Seed seed, bool consumed, CancellationToken ct, string? expectedEmail = null)
    {
        await using var observation = CreateContext(connectionString);
        var user = await observation.Users.AsNoTracking().SingleAsync(u => u.Id == seed.UserId, ct);
        var token = await observation.EmailChangeTokens.AsNoTracking().SingleAsync(t => t.Id == seed.TokenId, ct);
        Assert.Equal(expectedEmail ?? (consumed ? NewEmail : OldEmail), user.PrimaryEmail);
        Assert.Equal(consumed ? (DateTimeOffset?)Now : null, token.ConsumedAt);
        Assert.Null(token.SupersededAt);
        Assert.Equal(NewEmail, token.NewEmail);
    }

    private static bool UpdatesUser(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("UPDATE \"User\"", StringComparison.Ordinal);

    private sealed class AfterUserUpdateInterceptor(Action<CancellationToken> afterUpdate) : DbCommandInterceptor
    {
        public int ExecutedUpdates { get; private set; }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (UpdatesUser(command))
            {
                // This callback runs AFTER PostgreSQL executed the command, not before it.
                Assert.Equal(1, result);
                ExecutedUpdates++;
                afterUpdate(cancellationToken);
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class BeforeFirstUpdateInterceptor(
        Func<CancellationToken, Task> beforeUpdate, bool userOnly = false) : DbCommandInterceptor
    {
        private int _entries;
        public int Entries => Volatile.Read(ref _entries);
        public ConcurrentQueue<int> UserUpdateResults { get; } = new();
        public ConcurrentQueue<int> TokenUpdateResults { get; } = new();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var matches = userOnly
                ? UpdatesUser(command)
                : command.CommandText.TrimStart().StartsWith("UPDATE ", StringComparison.Ordinal);
            if (matches && Interlocked.CompareExchange(ref _entries, 1, 0) == 0)
                await beforeUpdate(cancellationToken).WaitAsync(WaitLimit, cancellationToken);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (UpdatesUser(command))
                UserUpdateResults.Enqueue(result);
            if (command.CommandText.TrimStart().StartsWith("UPDATE \"EmailChangeToken\"", StringComparison.Ordinal))
                TokenUpdateResults.Enqueue(result);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TwoRequestGate
    {
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                Release();
            await _ready.Task.WaitAsync(WaitLimit, ct);
        }

        public void Release() => _ready.TrySetResult(true);
    }

    private sealed class RecordingSender(
        Func<CancellationToken, Task>? beforeAlert = null, bool failAlerts = false) : IEmailChangeEmailSender
    {
        public ConcurrentQueue<(string OldEmail, string NewEmail)> Alerts { get; } = new();
        public int InjectedFailures { get; private set; }

        public Task SendVerificationLinkAsync(string toEmail, Uri verifyUrl, TimeSpan ttl, CancellationToken ct) =>
            throw new InvalidOperationException("ConfirmAsync must not send verification links.");

        public async Task SendSecurityAlertAsync(string toEmail, string newEmail, CancellationToken ct)
        {
            Alerts.Enqueue((toEmail, newEmail));
            if (beforeAlert is not null)
                await beforeAlert(ct);
            if (failAlerts)
            {
                InjectedFailures++;
                throw new ApplicationException("Injected security-alert delivery failure.");
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Seed(UserId UserId, Guid TokenId, string RawToken);

    private static async Task WithFreshDatabaseAsync(Func<string, CancellationToken, Task> test)
    {
        var configured = Environment.GetEnvironmentVariable("KORAT_TEST_POSTGRES");
        PostgreSqlContainer? postgres = null;
        NpgsqlConnectionStringBuilder? admin = null;
        var databaseName = $"korat_email_atomic_{Guid.NewGuid():N}";
        var created = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
                    await postgres.StartAsync(deadline.Token);
                    configured = postgres.GetConnectionString();
                }
                catch (Exception ex)
                {
                    throw new SkipException($"Docker/PostgreSQL 16 unavailable: {ex.GetType().Name}");
                }
            }

            admin = new NpgsqlConnectionStringBuilder(configured)
            {
                Pooling = false,
                Timeout = 10,
                CommandTimeout = 15,
                Options = "-c statement_timeout=15000 -c lock_timeout=10000",
            };
            Assert.True(admin.Host is "127.0.0.1" or "localhost" or "::1",
                "Only a local disposable PostgreSQL server may be used.");
            var isolated = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = databaseName };

            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync(deadline.Token);
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
                await create.ExecuteNonQueryAsync(deadline.Token);
                created = true;
            }

            await test(isolated.ConnectionString, deadline.Token);
        }
        finally
        {
            try
            {
                if (created && admin is not null)
                {
                    // Cleanup must not inherit a cancelled request/test deadline, nor clear
                    // global pools belonging to other concurrently running tests.
                    using var cleanup = new CancellationTokenSource(WaitLimit);
                    await using var connection = new NpgsqlConnection(admin.ConnectionString);
                    await connection.OpenAsync(cleanup.Token);
                    await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", connection);
                    await drop.ExecuteNonQueryAsync(cleanup.Token);
                }
            }
            finally
            {
                if (postgres is not null)
                    await postgres.DisposeAsync().AsTask().WaitAsync(WaitLimit);
            }
        }
    }
}
