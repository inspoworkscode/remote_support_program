using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
{
    /// <inheritdoc />
    public partial class PgInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TechnicianId = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MatchKey = table.Column<string>(type: "text", nullable: false),
                    LastPartNumber = table.Column<string>(type: "text", nullable: false),
                    MergedIntoId = table.Column<int>(type: "integer", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Chat",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    From = table.Column<string>(type: "text", nullable: false),
                    FromTech = table.Column<bool>(type: "boolean", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chat", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HealthCheck = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRemovals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    RemovedBy = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRemovals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboundMail",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    FromName = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: true),
                    TicketNumber = table.Column<int>(type: "integer", nullable: false),
                    ReplyResult = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMail", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbImages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBy = table.Column<string>(type: "text", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbImages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbRevisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ArticleId = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    FolderId = table.Column<int>(type: "integer", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: false),
                    SavedBy = table.Column<string>(type: "text", nullable: false),
                    SavedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbRevisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutsiderCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    ForWhom = table.Column<string>(type: "text", nullable: false),
                    MadeBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedBy = table.Column<string>(type: "text", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedWhy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsiderCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SmtpHost = table.Column<string>(type: "text", nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUser = table.Column<string>(type: "text", nullable: false),
                    SmtpPasswordEnc = table.Column<string>(type: "text", nullable: false),
                    SmtpFrom = table.Column<string>(type: "text", nullable: false),
                    SmtpFromName = table.Column<string>(type: "text", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    NoticeCopyTo = table.Column<string>(type: "text", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OwnerTotpSecretEnc = table.Column<string>(type: "text", nullable: false),
                    OwnerTotpLastStep = table.Column<long>(type: "bigint", nullable: false),
                    IntakeAddress = table.Column<string>(type: "text", nullable: false),
                    IntakeImapHost = table.Column<string>(type: "text", nullable: false),
                    IntakeImapPort = table.Column<int>(type: "integer", nullable: false),
                    IntakeImapSecurity = table.Column<string>(type: "text", nullable: false),
                    IntakeImapUser = table.Column<string>(type: "text", nullable: false),
                    IntakeImapPasswordEnc = table.Column<string>(type: "text", nullable: false),
                    IntakeUidValidity = table.Column<long>(type: "bigint", nullable: false),
                    IntakeLastUid = table.Column<long>(type: "bigint", nullable: false),
                    IntakeLastCheckUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntakeLastProblem = table.Column<string>(type: "text", nullable: false),
                    IntakePaused = table.Column<bool>(type: "boolean", nullable: false),
                    IntakeFailures = table.Column<int>(type: "integer", nullable: false),
                    IntakeNextTryUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuoteLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: false),
                    PartNumber = table.Column<string>(type: "text", nullable: false),
                    SerialNumber = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    PhotoId = table.Column<long>(type: "bigint", nullable: true),
                    AddedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteLines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuoteSignatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    LinesJson = table.Column<string>(type: "text", nullable: false),
                    Image = table.Column<byte[]>(type: "bytea", nullable: true),
                    ImageType = table.Column<string>(type: "text", nullable: false),
                    ImageToken = table.Column<string>(type: "text", nullable: false),
                    ImageWidth = table.Column<int>(type: "integer", nullable: false),
                    ImageFirst = table.Column<bool>(type: "boolean", nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteSignatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringKinds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MatchKey = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringKinds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    AssignedTo = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Repeats = table.Column<int>(type: "integer", nullable: false),
                    WeekdaysOnly = table.Column<bool>(type: "boolean", nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: false),
                    MonthDay = table.Column<int>(type: "integer", nullable: false),
                    NextDue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastOpenedFor = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "text", nullable: false),
                    CancelReason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeatAgreements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    People = table.Column<int>(type: "integer", nullable: false),
                    Included = table.Column<int>(type: "integer", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    Monthly = table.Column<decimal>(type: "numeric", nullable: false),
                    MonthlyBefore = table.Column<decimal>(type: "numeric", nullable: false),
                    What = table.Column<string>(type: "text", nullable: false),
                    AgreedBy = table.Column<string>(type: "text", nullable: false),
                    AgreedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeatAgreements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SessionRecordings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    RecordedBy = table.Column<string>(type: "text", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionRecordings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: true),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    Technicians = table.Column<string>(type: "text", nullable: false),
                    StartedBy = table.Column<string>(type: "text", nullable: false),
                    Unattended = table.Column<bool>(type: "boolean", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffChatReads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Person = table.Column<string>(type: "text", nullable: false),
                    With = table.Column<string>(type: "text", nullable: false),
                    LastReadId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffChatReads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    From = table.Column<string>(type: "text", nullable: false),
                    To = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MatchKey = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    ContactPerson = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    CompanyName = table.Column<string>(type: "text", nullable: false),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    ContactNumber = table.Column<string>(type: "text", nullable: false),
                    Country = table.Column<string>(type: "text", nullable: false),
                    ProductName = table.Column<string>(type: "text", nullable: false),
                    SupportEmail = table.Column<string>(type: "text", nullable: false),
                    SupportPhone = table.Column<string>(type: "text", nullable: false),
                    PrimaryColor = table.Column<string>(type: "text", nullable: false),
                    AccentColor = table.Column<string>(type: "text", nullable: false),
                    WelcomeText = table.Column<string>(type: "text", nullable: false),
                    TechnicianTheme = table.Column<string>(type: "text", nullable: false),
                    ClientTheme = table.Column<string>(type: "text", nullable: false),
                    LogoFile = table.Column<string>(type: "text", nullable: false),
                    TenantKey = table.Column<string>(type: "text", nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    TimeZone = table.Column<string>(type: "text", nullable: false),
                    NotBilled = table.Column<bool>(type: "boolean", nullable: false),
                    RequireConsent = table.Column<bool>(type: "boolean", nullable: false),
                    UnattendedRequireConsent = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Plan = table.Column<string>(type: "text", nullable: false),
                    BillingNotes = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrialEndsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrialNoticesSent = table.Column<string>(type: "text", nullable: false),
                    LastTrialNoticeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HealthCheckEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    HealthCheckEmailTo = table.Column<string>(type: "text", nullable: false),
                    HealthCheckDayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    HealthCheckRunMaintenance = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpHost = table.Column<string>(type: "text", nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUser = table.Column<string>(type: "text", nullable: false),
                    SmtpPasswordEnc = table.Column<string>(type: "text", nullable: false),
                    SmtpFrom = table.Column<string>(type: "text", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    QuoteToAddress = table.Column<string>(type: "text", nullable: false),
                    QuoteDeliveryAddress = table.Column<string>(type: "text", nullable: false),
                    WebSupportEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    OutsidersAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    WebSupportHours = table.Column<string>(type: "text", nullable: false),
                    WebSupportTimeZone = table.Column<string>(type: "text", nullable: false),
                    WebChatEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    WebChatHours = table.Column<string>(type: "text", nullable: false),
                    WebChatTimeZone = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TermsAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    Draft = table.Column<bool>(type: "boolean", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    AcceptedByName = table.Column<string>(type: "text", nullable: false),
                    AcceptedByEmail = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    AcceptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TermsAcceptances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketClientUpdates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    NoteId = table.Column<long>(type: "bigint", nullable: false),
                    ToAddress = table.Column<string>(type: "text", nullable: false),
                    ToName = table.Column<string>(type: "text", nullable: false),
                    SentBy = table.Column<string>(type: "text", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Sent = table.Column<bool>(type: "boolean", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketClientUpdates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketIntake",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Way = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: false),
                    AddressConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ConfirmTokenHash = table.Column<string>(type: "text", nullable: false),
                    ConfirmSentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImapHost = table.Column<string>(type: "text", nullable: false),
                    ImapPort = table.Column<int>(type: "integer", nullable: false),
                    ImapSecurity = table.Column<string>(type: "text", nullable: false),
                    ImapUser = table.Column<string>(type: "text", nullable: false),
                    ImapPasswordEnc = table.Column<string>(type: "text", nullable: false),
                    Folder = table.Column<string>(type: "text", nullable: false),
                    SendVia = table.Column<string>(type: "text", nullable: false),
                    SmtpSameLogin = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpHost = table.Column<string>(type: "text", nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpSecurity = table.Column<string>(type: "text", nullable: false),
                    SmtpUser = table.Column<string>(type: "text", nullable: false),
                    SmtpPasswordEnc = table.Column<string>(type: "text", nullable: false),
                    AutoReply = table.Column<bool>(type: "boolean", nullable: false),
                    AutoReplyText = table.Column<string>(type: "text", nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    LastUid = table.Column<long>(type: "bigint", nullable: false),
                    LastCheckUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastOkUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastProblem = table.Column<string>(type: "text", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    NextTryUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GmailConfirmation = table.Column<string>(type: "text", nullable: false),
                    GmailConfirmationUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FormEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    FormHeading = table.Column<string>(type: "text", nullable: false),
                    FormIntro = table.Column<string>(type: "text", nullable: false),
                    FormPhone = table.Column<string>(type: "text", nullable: false),
                    FormCompany = table.Column<bool>(type: "boolean", nullable: false),
                    FormThanks = table.Column<string>(type: "text", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketIntake", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WaitingList",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Region = table.Column<string>(type: "text", nullable: false),
                    Country = table.Column<string>(type: "text", nullable: false),
                    CompanyName = table.Column<string>(type: "text", nullable: false),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitingList", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebChatLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    WebChatId = table.Column<int>(type: "integer", nullable: false),
                    FromVisitor = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebChatLines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebChats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Page = table.Column<string>(type: "text", nullable: false),
                    Ip = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ContactId = table.Column<int>(type: "integer", nullable: true),
                    AnsweredBy = table.Column<string>(type: "text", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedBy = table.Column<string>(type: "text", nullable: false),
                    LeftMessage = table.Column<bool>(type: "boolean", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebChats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbArticles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    FolderId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    FromTicketId = table.Column<int>(type: "integer", nullable: true),
                    FromTicketNumber = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbArticles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbArticles_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HealthCheckRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Critical = table.Column<int>(type: "integer", nullable: false),
                    Warning = table.Column<int>(type: "integer", nullable: false),
                    Info = table.Column<int>(type: "integer", nullable: false),
                    Ok = table.Column<int>(type: "integer", nullable: false),
                    MaintenanceRan = table.Column<bool>(type: "boolean", nullable: false),
                    RestorePointMade = table.Column<bool>(type: "boolean", nullable: false),
                    JsonFile = table.Column<string>(type: "text", nullable: false),
                    PdfFile = table.Column<string>(type: "text", nullable: false),
                    MailStatus = table.Column<string>(type: "text", nullable: false),
                    MailedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HealthCheckRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Technicians",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Salt = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Appearance = table.Column<string>(type: "text", nullable: false),
                    MailClient = table.Column<string>(type: "text", nullable: false),
                    LastQuoteSignatureId = table.Column<int>(type: "integer", nullable: true),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: false),
                    TotpSecretEnc = table.Column<string>(type: "text", nullable: false),
                    TotpEnabledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotpLastStep = table.Column<long>(type: "bigint", nullable: false),
                    RecoveryCodeHashes = table.Column<string>(type: "text", nullable: false),
                    SignInPlaces = table.Column<string>(type: "text", nullable: false),
                    BrowserSignIn = table.Column<string>(type: "text", nullable: false),
                    BrowserDevice = table.Column<string>(type: "text", nullable: false),
                    TrustStamp = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Technicians", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Technicians_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    DeviceKey = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    Os = table.Column<string>(type: "text", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Cpu = table.Column<string>(type: "text", nullable: false),
                    Cores = table.Column<int>(type: "integer", nullable: false),
                    RamMb = table.Column<int>(type: "integer", nullable: false),
                    DiskTotalGb = table.Column<int>(type: "integer", nullable: false),
                    DiskFreeGb = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ContactId = table.Column<int>(type: "integer", nullable: true),
                    HardwareId = table.Column<string>(type: "text", nullable: false),
                    ReinstallCount = table.Column<int>(type: "integer", nullable: false),
                    LastReinstallUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReinstallUnconfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ReinstallConfirmedBy = table.Column<string>(type: "text", nullable: false),
                    ReinstallConfirmedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnattendedEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UnattendedChangedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnattendedChangedBy = table.Column<string>(type: "text", nullable: false),
                    UnattendedPasswordHash = table.Column<string>(type: "text", nullable: false),
                    UnattendedPasswordSalt = table.Column<string>(type: "text", nullable: false),
                    UnattendedPasswordChangedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnattendedPasswordChangedBy = table.Column<string>(type: "text", nullable: false),
                    ClosedByUserUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HealthCheckMode = table.Column<int>(type: "integer", nullable: false),
                    OutsiderMode = table.Column<int>(type: "integer", nullable: false),
                    NetworkCardsJson = table.Column<string>(type: "text", nullable: false),
                    PublicAddress = table.Column<string>(type: "text", nullable: false),
                    HealthCheckInstalled = table.Column<bool>(type: "boolean", nullable: true),
                    HealthCheckNote = table.Column<string>(type: "text", nullable: false),
                    HealthCheckReportedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Devices_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Devices_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ContactId = table.Column<int>(type: "integer", nullable: true),
                    ParentTicketId = table.Column<int>(type: "integer", nullable: true),
                    SiteAddress = table.Column<string>(type: "text", nullable: false),
                    AppointmentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssetMake = table.Column<string>(type: "text", nullable: false),
                    AssetModel = table.Column<string>(type: "text", nullable: false),
                    AssetSerial = table.Column<string>(type: "text", nullable: false),
                    Accessories = table.Column<string>(type: "text", nullable: false),
                    DevicePasswordProtected = table.Column<bool>(type: "boolean", nullable: false),
                    DevicePasswordEnc = table.Column<string>(type: "text", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    Lane = table.Column<int>(type: "integer", nullable: false),
                    StageReturn = table.Column<string>(type: "text", nullable: false),
                    Repairable = table.Column<bool>(type: "boolean", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    WorkedBy = table.Column<string>(type: "text", nullable: false),
                    TimeSpentMinutes = table.Column<int>(type: "integer", nullable: true),
                    InvoiceAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    InvoiceNumber = table.Column<string>(type: "text", nullable: false),
                    PaymentMethod = table.Column<string>(type: "text", nullable: false),
                    PaymentReference = table.Column<string>(type: "text", nullable: false),
                    QuoteAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    QuoteReference = table.Column<string>(type: "text", nullable: false),
                    QuoteSupplierEmail = table.Column<string>(type: "text", nullable: false),
                    QuoteSupplierNames = table.Column<string>(type: "text", nullable: false),
                    QuoteSignatureId = table.Column<int>(type: "integer", nullable: true),
                    QuoteMachineSaved = table.Column<bool>(type: "boolean", nullable: false),
                    QuoteMachineType = table.Column<string>(type: "text", nullable: false),
                    QuoteMachineMake = table.Column<string>(type: "text", nullable: false),
                    QuoteMachineModel = table.Column<string>(type: "text", nullable: false),
                    QuoteMachineSerial = table.Column<string>(type: "text", nullable: false),
                    DoNotInvoiceReason = table.Column<string>(type: "text", nullable: false),
                    AbandonedReason = table.Column<string>(type: "text", nullable: false),
                    CollectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CollectedBy = table.Column<string>(type: "text", nullable: false),
                    Delivered = table.Column<bool>(type: "boolean", nullable: false),
                    BookingId = table.Column<int>(type: "integer", nullable: true),
                    AssignedTo = table.Column<string>(type: "text", nullable: false),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    RequesterEmail = table.Column<string>(type: "text", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    OpenedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecurringTaskId = table.Column<int>(type: "integer", nullable: true),
                    DueOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tickets_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tickets_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tickets_Tickets_ParentTicketId",
                        column: x => x.ParentTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SavedUnattendedPasswords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    DeviceRowId = table.Column<int>(type: "integer", nullable: false),
                    TechnicianId = table.Column<int>(type: "integer", nullable: false),
                    PasswordSalt = table.Column<string>(type: "text", nullable: false),
                    SavedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedUnattendedPasswords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedUnattendedPasswords_Devices_DeviceRowId",
                        column: x => x.DeviceRowId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedUnattendedPasswords_Technicians_TechnicianId",
                        column: x => x.TechnicianId,
                        principalTable: "Technicians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketMachines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    Make = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    Serial = table.Column<string>(type: "text", nullable: false),
                    Accessories = table.Column<string>(type: "text", nullable: false),
                    PasswordProtected = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordEnc = table.Column<string>(type: "text", nullable: false),
                    AddedBy = table.Column<string>(type: "text", nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketMachines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketMachines_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    Author = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    IsWorkDone = table.Column<bool>(type: "boolean", nullable: false),
                    FormTitle = table.Column<string>(type: "text", nullable: false),
                    MovedTo = table.Column<string>(type: "text", nullable: false),
                    FromCustomer = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketNotes_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketPhotos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    OriginalName = table.Column<string>(type: "text", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBy = table.Column<string>(type: "text", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StageId = table.Column<string>(type: "text", nullable: false),
                    StagedBy = table.Column<string>(type: "text", nullable: false),
                    MachineId = table.Column<int>(type: "integer", nullable: true),
                    QuoteLineId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketPhotos_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketMentions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    NoteId = table.Column<long>(type: "bigint", nullable: false),
                    Person = table.Column<string>(type: "text", nullable: false),
                    MentionedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketMentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketMentions_TicketNotes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "TicketNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketMentions_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountTokens_TechnicianId",
                table: "AccountTokens",
                column: "TechnicianId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountTokens_TokenHash",
                table: "AccountTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_TenantId_MatchKey",
                table: "CatalogItems",
                columns: new[] { "TenantId", "MatchKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Chat_TenantId_TsUtc",
                table: "Chat",
                columns: new[] { "TenantId", "TsUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_TenantId_Name",
                table: "Clients",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_ClientId",
                table: "Contacts",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_ClientId",
                table: "Contacts",
                columns: new[] { "TenantId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_Name",
                table: "Contacts",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRemovals_TenantId_DeviceId",
                table: "DeviceRemovals",
                columns: new[] { "TenantId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRemovals_TenantId_RemovedUtc",
                table: "DeviceRemovals",
                columns: new[] { "TenantId", "RemovedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ClientId",
                table: "Devices",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ContactId",
                table: "Devices",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId_DeviceId",
                table: "Devices",
                columns: new[] { "TenantId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId_HardwareId",
                table: "Devices",
                columns: new[] { "TenantId", "HardwareId" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckRuns_TenantId_DeviceId_RunUtc",
                table: "HealthCheckRuns",
                columns: new[] { "TenantId", "DeviceId", "RunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckRuns_TenantId_ReceivedUtc",
                table: "HealthCheckRuns",
                columns: new[] { "TenantId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMail_TenantId_FromAddress_ReceivedUtc",
                table: "InboundMail",
                columns: new[] { "TenantId", "FromAddress", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMail_TenantId_Id",
                table: "InboundMail",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMail_TenantId_MessageId",
                table: "InboundMail",
                columns: new[] { "TenantId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_ClientId",
                table: "KbArticles",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_TenantId_ClientId",
                table: "KbArticles",
                columns: new[] { "TenantId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_TenantId_FolderId",
                table: "KbArticles",
                columns: new[] { "TenantId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_TenantId_FromTicketId",
                table: "KbArticles",
                columns: new[] { "TenantId", "FromTicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbFolders_TenantId_ClientId",
                table: "KbFolders",
                columns: new[] { "TenantId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbRevisions_TenantId_ArticleId_Version",
                table: "KbRevisions",
                columns: new[] { "TenantId", "ArticleId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_OutsiderCodes_CodeHash",
                table: "OutsiderCodes",
                column: "CodeHash");

            migrationBuilder.CreateIndex(
                name: "IX_OutsiderCodes_TenantId_DeviceId_CreatedUtc",
                table: "OutsiderCodes",
                columns: new[] { "TenantId", "DeviceId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_TenantId_TicketId",
                table: "QuoteLines",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteSignatures_ImageToken",
                table: "QuoteSignatures",
                column: "ImageToken");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteSignatures_TenantId",
                table: "QuoteSignatures",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringKinds_TenantId_MatchKey",
                table: "RecurringKinds",
                columns: new[] { "TenantId", "MatchKey" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringTasks_TenantId_CancelledUtc_NextDue",
                table: "RecurringTasks",
                columns: new[] { "TenantId", "CancelledUtc", "NextDue" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedUnattendedPasswords_DeviceRowId_TechnicianId",
                table: "SavedUnattendedPasswords",
                columns: new[] { "DeviceRowId", "TechnicianId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedUnattendedPasswords_TechnicianId",
                table: "SavedUnattendedPasswords",
                column: "TechnicianId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedUnattendedPasswords_TenantId",
                table: "SavedUnattendedPasswords",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SeatAgreements_TenantId_AgreedUtc",
                table: "SeatAgreements",
                columns: new[] { "TenantId", "AgreedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TenantId_StartedUtc",
                table: "Sessions",
                columns: new[] { "TenantId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffChatReads_TenantId_Person_With",
                table: "StaffChatReads",
                columns: new[] { "TenantId", "Person", "With" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffMessages_TenantId_From_Id",
                table: "StaffMessages",
                columns: new[] { "TenantId", "From", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffMessages_TenantId_To_Id",
                table: "StaffMessages",
                columns: new[] { "TenantId", "To", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_MatchKey",
                table: "Suppliers",
                columns: new[] { "TenantId", "MatchKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Technicians_Email",
                table: "Technicians",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Technicians_TenantId",
                table: "Technicians",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_TenantKey",
                table: "Tenants",
                column: "TenantKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TermsAcceptances_TenantId",
                table: "TermsAcceptances",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketClientUpdates_TenantId_TicketId",
                table: "TicketClientUpdates",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketIntake_TenantId",
                table: "TicketIntake",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketMachines_TenantId_TicketId",
                table: "TicketMachines",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMachines_TicketId",
                table: "TicketMachines",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketMentions_NoteId",
                table: "TicketMentions",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketMentions_TenantId_Person_AnsweredUtc",
                table: "TicketMentions",
                columns: new[] { "TenantId", "Person", "AnsweredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMentions_TicketId",
                table: "TicketMentions",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketNotes_TenantId_TicketId",
                table: "TicketNotes",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketNotes_TicketId",
                table: "TicketNotes",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TenantId_QuoteLineId",
                table: "TicketPhotos",
                columns: new[] { "TenantId", "QuoteLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TenantId_StageId",
                table: "TicketPhotos",
                columns: new[] { "TenantId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TenantId_TicketId",
                table: "TicketPhotos",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TicketId",
                table: "TicketPhotos",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_UploadedUtc",
                table: "TicketPhotos",
                column: "UploadedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ClientId",
                table: "Tickets",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ContactId",
                table: "Tickets",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ParentTicketId",
                table: "Tickets",
                column: "ParentTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Category",
                table: "Tickets",
                columns: new[] { "TenantId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ClientId",
                table: "Tickets",
                columns: new[] { "TenantId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_DeviceId_CreatedUtc",
                table: "Tickets",
                columns: new[] { "TenantId", "DeviceId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Lane",
                table: "Tickets",
                columns: new[] { "TenantId", "Lane" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Number",
                table: "Tickets",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ParentTicketId",
                table: "Tickets",
                columns: new[] { "TenantId", "ParentTicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_RecurringTaskId_DueOn",
                table: "Tickets",
                columns: new[] { "TenantId", "RecurringTaskId", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_RequesterEmail",
                table: "Tickets",
                columns: new[] { "TenantId", "RequesterEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Status",
                table: "Tickets",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WaitingList_Region_Email",
                table: "WaitingList",
                columns: new[] { "Region", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_WebChatLines_WebChatId_Id",
                table: "WebChatLines",
                columns: new[] { "WebChatId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WebChats_TenantId_EndedUtc",
                table: "WebChats",
                columns: new[] { "TenantId", "EndedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountTokens");

            migrationBuilder.DropTable(
                name: "CatalogItems");

            migrationBuilder.DropTable(
                name: "Chat");

            migrationBuilder.DropTable(
                name: "DeviceRemovals");

            migrationBuilder.DropTable(
                name: "HealthCheckRuns");

            migrationBuilder.DropTable(
                name: "InboundMail");

            migrationBuilder.DropTable(
                name: "KbArticles");

            migrationBuilder.DropTable(
                name: "KbFolders");

            migrationBuilder.DropTable(
                name: "KbImages");

            migrationBuilder.DropTable(
                name: "KbRevisions");

            migrationBuilder.DropTable(
                name: "OutsiderCodes");

            migrationBuilder.DropTable(
                name: "PlatformSettings");

            migrationBuilder.DropTable(
                name: "QuoteLines");

            migrationBuilder.DropTable(
                name: "QuoteSignatures");

            migrationBuilder.DropTable(
                name: "RecurringKinds");

            migrationBuilder.DropTable(
                name: "RecurringTasks");

            migrationBuilder.DropTable(
                name: "SavedUnattendedPasswords");

            migrationBuilder.DropTable(
                name: "SeatAgreements");

            migrationBuilder.DropTable(
                name: "SessionRecordings");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "StaffChatReads");

            migrationBuilder.DropTable(
                name: "StaffMessages");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropTable(
                name: "TermsAcceptances");

            migrationBuilder.DropTable(
                name: "TicketClientUpdates");

            migrationBuilder.DropTable(
                name: "TicketIntake");

            migrationBuilder.DropTable(
                name: "TicketMachines");

            migrationBuilder.DropTable(
                name: "TicketMentions");

            migrationBuilder.DropTable(
                name: "TicketPhotos");

            migrationBuilder.DropTable(
                name: "WaitingList");

            migrationBuilder.DropTable(
                name: "WebChatLines");

            migrationBuilder.DropTable(
                name: "WebChats");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Technicians");

            migrationBuilder.DropTable(
                name: "TicketNotes");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "Clients");
        }
    }
}
