using System.Text.Json.Serialization;

namespace RemoteSupport.Shared;

// ---------------------------------------------------------------------------
// Control-plane messages (JSON text frames). Every message carries a "t" field.
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
// device (client app / unattended agent / spawned host helper) -> server
[JsonDerivedType(typeof(HelloMsg), "hello")]
[JsonDerivedType(typeof(RequestCallMsg), "request_call")]
[JsonDerivedType(typeof(CancelCallMsg), "cancel_call")]
// technician -> server
[JsonDerivedType(typeof(AcceptCallMsg), "accept_call")]
[JsonDerivedType(typeof(JoinSessionMsg), "join_session")]
[JsonDerivedType(typeof(ConnectDeviceMsg), "connect_device")]
[JsonDerivedType(typeof(EndSessionMsg), "end_session")]
// both directions
[JsonDerivedType(typeof(ChatMsg), "chat")]
[JsonDerivedType(typeof(PingMsg), "ping")]
// server -> technician
[JsonDerivedType(typeof(StateMsg), "state")]
[JsonDerivedType(typeof(RingMsg), "ring")]
[JsonDerivedType(typeof(CallUpdateMsg), "call_update")]
[JsonDerivedType(typeof(DeviceUpdateMsg), "device_update")]
[JsonDerivedType(typeof(SessionReadyMsg), "session_ready")]
[JsonDerivedType(typeof(TechPresenceMsg), "tech_presence")]
[JsonDerivedType(typeof(SetUnattendedMsg), "set_unattended")]
[JsonDerivedType(typeof(SetUnattendedPasswordMsg), "set_unattended_password")]
// ---- outside help (roadmap 3.9): somebody outside the company, let in by the customer with a code ----
[JsonDerivedType(typeof(OutsiderStateMsg), "outsider_state")]
[JsonDerivedType(typeof(OutsiderCodeRequestMsg), "outsider_code_req")]
[JsonDerivedType(typeof(OutsiderCodeMsg), "outsider_code")]
[JsonDerivedType(typeof(OutsiderCodeCancelMsg), "outsider_code_cancel")]
[JsonDerivedType(typeof(DeviceOutsiderModeMsg), "device_outsider_mode")]
[JsonDerivedType(typeof(OutsiderConnectMsg), "outsider_connect")]
[JsonDerivedType(typeof(OutsiderVisitMsg), "outsider_visit")]
[JsonDerivedType(typeof(GuestConnectMsg), "guest_connect")]
[JsonDerivedType(typeof(GuestTicketMsg), "guest_ticket")]
// ---- outside help asked for by a TECHNICIAN in a session (2026-10-22, §155) ----
[JsonDerivedType(typeof(OutsiderInviteRequestMsg), "outsider_invite_req")]
[JsonDerivedType(typeof(OutsiderInviteMsg), "outsider_invite")]
[JsonDerivedType(typeof(OutsiderInviteCancelMsg), "outsider_invite_cancel")]
[JsonDerivedType(typeof(OutsiderJoinAskMsg), "outsider_join_ask")]
[JsonDerivedType(typeof(OutsiderJoinAnswerMsg), "outsider_join_answer")]
[JsonDerivedType(typeof(OutsiderRemoveMsg), "outsider_remove")]
[JsonDerivedType(typeof(OutsiderJoinedMsg), "outsider_joined")]
[JsonDerivedType(typeof(ClientClosingMsg), "client_closing")]
// server -> device
[JsonDerivedType(typeof(StartSessionMsg), "start_session")]
[JsonDerivedType(typeof(SessionEndedMsg), "session_ended")]
[JsonDerivedType(typeof(SessionStateMsg), "session_state")]
[JsonDerivedType(typeof(UnattendedStateMsg), "unattended_state")]
[JsonDerivedType(typeof(DeclineSessionMsg), "decline_session")]
[JsonDerivedType(typeof(ConsentGrantedMsg), "consent_granted")]
[JsonDerivedType(typeof(ErrorMsg), "error")]
// ---- in-session messages (session websocket) ----
[JsonDerivedType(typeof(MonitorsMsg), "monitors")]
[JsonDerivedType(typeof(SubscribeMsg), "subscribe")]
[JsonDerivedType(typeof(QualityMsg), "quality")]
[JsonDerivedType(typeof(MouseMsg), "mouse")]
[JsonDerivedType(typeof(KeyMsg), "key")]
[JsonDerivedType(typeof(CursorMsg), "cursor")]
[JsonDerivedType(typeof(FileOfferMsg), "file_offer")]
[JsonDerivedType(typeof(FileAcceptMsg), "file_accept")]
[JsonDerivedType(typeof(FileDoneMsg), "file_done")]
[JsonDerivedType(typeof(FileListRequestMsg), "file_list_req")]
[JsonDerivedType(typeof(FileListMsg), "file_list")]
[JsonDerivedType(typeof(FilePullMsg), "file_pull")]
[JsonDerivedType(typeof(FileSetOfferMsg), "fileset_offer")]
[JsonDerivedType(typeof(FileSetAcceptMsg), "fileset_accept")]
[JsonDerivedType(typeof(FileSetProgressMsg), "fileset_progress")]
[JsonDerivedType(typeof(FileSetDoneMsg), "fileset_done")]
[JsonDerivedType(typeof(FileSetPullMsg), "fileset_pull")]
[JsonDerivedType(typeof(SelectionRequestMsg), "selection_request")]
[JsonDerivedType(typeof(SelectionMsg), "selection")]
[JsonDerivedType(typeof(PrivacyScreenMsg), "privacy_screen")]
[JsonDerivedType(typeof(PrivacyStateMsg), "privacy_state")]
[JsonDerivedType(typeof(RecordingStateMsg), "recording_state")]
[JsonDerivedType(typeof(FileActivityMsg), "file_activity")]
[JsonDerivedType(typeof(ScriptRunMsg), "script_run")]
[JsonDerivedType(typeof(ScriptOutputMsg), "script_output")]
[JsonDerivedType(typeof(ScriptKillMsg), "script_kill")]
[JsonDerivedType(typeof(ParticipantsMsg), "participants")]
[JsonDerivedType(typeof(ClipboardMsg), "clipboard")]
[JsonDerivedType(typeof(ClipboardRequestMsg), "clipboard_req")]
[JsonDerivedType(typeof(SysInfoMsg), "sysinfo")]
[JsonDerivedType(typeof(SysActionMsg), "sys_action")]
[JsonDerivedType(typeof(SysActionResultMsg), "sys_action_result")]
[JsonDerivedType(typeof(AudioListenMsg), "audio_listen")]
[JsonDerivedType(typeof(AudioStatusMsg), "audio_status")]
[JsonDerivedType(typeof(AudioDevicesRequestMsg), "audio_devices_req")]
[JsonDerivedType(typeof(AudioDevicesMsg), "audio_devices")]
[JsonDerivedType(typeof(VoiceTalkMsg), "voice_talk")]
[JsonDerivedType(typeof(VoiceStateMsg), "voice_state")]
[JsonDerivedType(typeof(VoiceMicAnswerMsg), "voice_mic_answer")]
// ---- ticketing (technician <-> server, technician-only) ----
[JsonDerivedType(typeof(TicketListRequestMsg), "ticket_list_req")]
[JsonDerivedType(typeof(TicketListMsg), "ticket_list")]
[JsonDerivedType(typeof(TicketOpenMsg), "ticket_open")]
[JsonDerivedType(typeof(TicketDetailMsg), "ticket_detail")]
[JsonDerivedType(typeof(TicketCreateMsg), "ticket_create")]
[JsonDerivedType(typeof(TicketUpdateMsg), "ticket_update")]
[JsonDerivedType(typeof(TicketSetClientMsg), "ticket_set_client")]
[JsonDerivedType(typeof(TicketSetParentMsg), "ticket_set_parent")]
[JsonDerivedType(typeof(TicketEditMsg), "ticket_edit")]
[JsonDerivedType(typeof(TicketNoteAddMsg), "ticket_note_add")]
[JsonDerivedType(typeof(TicketChangedMsg), "ticket_changed")]
[JsonDerivedType(typeof(ClientSearchMsg), "client_search")]
[JsonDerivedType(typeof(ClientListMsg), "client_list")]
// ---- saying who a machine belongs to, from the console ----
[JsonDerivedType(typeof(DeviceLinkMsg), "device_link")]
// ---- opening the portal as the person already signed in to the console ----
[JsonDerivedType(typeof(PortalLinkRequestMsg), "portal_link_req")]
[JsonDerivedType(typeof(PortalLinkMsg), "portal_link")]
[JsonDerivedType(typeof(MentionCountMsg), "mention_count")]
[JsonDerivedType(typeof(BillingQueueMsg), "billing_queue")]
// ---- Latest activity (2026-10-26, HANDOVER §164; Activity.cs) ----
[JsonDerivedType(typeof(ActivityRequestMsg), "activity_req")]
[JsonDerivedType(typeof(ActivityListMsg), "activity_list")]
[JsonDerivedType(typeof(ActivityCountMsg), "activity_count")]
// ---- the knowledge base (3.8.5) ----
[JsonDerivedType(typeof(KnowledgeCountRequestMsg), "knowledge_count_req")]
[JsonDerivedType(typeof(KnowledgeCountMsg), "knowledge_count")]
[JsonDerivedType(typeof(StaffChatSendMsg), "staff_chat_send")]
[JsonDerivedType(typeof(StaffChatMsg), "staff_chat")]
[JsonDerivedType(typeof(StaffChatHistoryRequestMsg), "staff_chat_history_req")]
[JsonDerivedType(typeof(StaffChatHistoryMsg), "staff_chat_history")]
[JsonDerivedType(typeof(StaffChatReadMsg), "staff_chat_read")]
[JsonDerivedType(typeof(StaffChatOverviewRequestMsg), "staff_chat_overview_req")]
[JsonDerivedType(typeof(StaffChatOverviewMsg), "staff_chat_overview")]
[JsonDerivedType(typeof(WebChatListMsg), "web_chat_list")]
[JsonDerivedType(typeof(WebChatUpdateMsg), "web_chat_update")]
[JsonDerivedType(typeof(WebChatLineMsg), "web_chat_line")]
[JsonDerivedType(typeof(WebChatHistoryRequestMsg), "web_chat_history_req")]
[JsonDerivedType(typeof(WebChatHistoryMsg), "web_chat_history")]
[JsonDerivedType(typeof(WebChatSayMsg), "web_chat_say")]
[JsonDerivedType(typeof(WebChatEndMsg), "web_chat_end")]
[JsonDerivedType(typeof(WebChatHandOverMsg), "web_chat_hand_over")]
[JsonDerivedType(typeof(WebChatPeopleRequestMsg), "web_chat_people_req")]
[JsonDerivedType(typeof(WebChatPeopleMsg), "web_chat_people")]
// ---- the workflow, from the console (stage 3.7) ----
[JsonDerivedType(typeof(TicketActMsg), "ticket_act")]
[JsonDerivedType(typeof(TicketWorkDoneMsg), "ticket_work_done")]
[JsonDerivedType(typeof(TicketNoticeMsg), "ticket_notice")]
[JsonDerivedType(typeof(TicketEmailNoteMsg), "ticket_email_note")]
[JsonDerivedType(typeof(RecurringKindsRequestMsg), "recurring_kinds_req")]
[JsonDerivedType(typeof(RecurringKindsMsg), "recurring_kinds")]
[JsonDerivedType(typeof(RecurringCancelMsg), "recurring_cancel")]
[JsonDerivedType(typeof(RecurringSetMsg), "recurring_set")]
[JsonDerivedType(typeof(ItemSuggestMsg), "item_suggest")]
[JsonDerivedType(typeof(ItemListMsg), "item_list")]
[JsonDerivedType(typeof(QuoteLineAddMsg), "quote_line_add")]
[JsonDerivedType(typeof(QuoteLineRemoveMsg), "quote_line_remove")]
[JsonDerivedType(typeof(QuoteLinePhotosMsg), "quote_line_photos")]
[JsonDerivedType(typeof(QuoteSimilarMsg), "quote_similar")]
[JsonDerivedType(typeof(QuoteMachineMsg), "quote_machine")]
// ---- the monthly health check, per client with a per-machine override (2026-09-28) ----
[JsonDerivedType(typeof(DeviceHealthCheckMsg), "device_health_check")]
[JsonDerivedType(typeof(HealthCheckWantedMsg), "health_check_wanted")]
[JsonDerivedType(typeof(HealthCheckStatusMsg), "health_check_status")]
// ---- signing in safely (2026-10-01) ----
[JsonDerivedType(typeof(SignedOutMsg), "signed_out")]
// ---- a look at a machine without connecting: Processes and Performance (2026-10-05) ----
[JsonDerivedType(typeof(WatchMachineMsg), "watch_machine")]
[JsonDerivedType(typeof(WatchRunMsg), "watch_run")]
[JsonDerivedType(typeof(WatchSampleMsg), "watch_sample")]
[JsonDerivedType(typeof(WatchEndedMsg), "watch_ended")]
// ---- Wake-on-LAN, through another machine on the same network (2026-10-06) ----
[JsonDerivedType(typeof(WakeRequestMsg), "wake_request")]
[JsonDerivedType(typeof(WakeSendMsg), "wake_send")]
[JsonDerivedType(typeof(WakeSentMsg), "wake_sent")]
[JsonDerivedType(typeof(WakeResultMsg), "wake_result")]
// ---- remote printing, both directions (2026-10-06) ----
[JsonDerivedType(typeof(RemotePrintMsg), "remote_print")]
[JsonDerivedType(typeof(RemotePrintStateMsg), "remote_print_state")]
[JsonDerivedType(typeof(RemotePrintJobMsg), "remote_print_job")]
[JsonDerivedType(typeof(PrintFileMsg), "print_file")]
[JsonDerivedType(typeof(PrintFileResultMsg), "print_file_result")]
// ---- remembered unattended passwords, per technician per machine (2026-10-06) ----
[JsonDerivedType(typeof(SavedPasswordsMsg), "saved_passwords")]
[JsonDerivedType(typeof(ForgetSavedPasswordMsg), "forget_saved_password")]
[JsonDerivedType(typeof(SavedPasswordWhoRequestMsg), "saved_password_who_req")]
[JsonDerivedType(typeof(SavedPasswordWhoMsg), "saved_password_who")]
// ---- the free version (2026-10-06) ----
[JsonDerivedType(typeof(DeviceRemoveMsg), "device_remove")]
[JsonDerivedType(typeof(DeviceGoneMsg), "device_gone")]
[JsonDerivedType(typeof(ClientUninstallingMsg), "client_uninstalling")]
// ---- a reinstalled machine confirmed by the office (2026-10-07) ----
[JsonDerivedType(typeof(ConfirmReinstallMsg), "confirm_reinstall")]
// ---- end-to-end encryption of sessions (2026-10-07, docs/NEXT-SESSION-E2E.md) ----
[JsonDerivedType(typeof(E2eReadyMsg), "e2e_ready")]
[JsonDerivedType(typeof(E2eHelloMsg), "e2e_hello")]
[JsonDerivedType(typeof(E2eKeyMsg), "e2e_key")]
[JsonDerivedType(typeof(E2eModeMsg), "e2e_mode")]
[JsonDerivedType(typeof(ScriptAuditMsg), "script_audit")]
// ---- a picture lost on the way to a viewer (2026-10-15, HANDOVER §142) ----
[JsonDerivedType(typeof(KeyFrameRequestMsg), "key_frame_request")]
// ---- branding reaches installed machines (2026-10-07, roadmap 4.6, the settings half) ----
[JsonDerivedType(typeof(BrandingMsg), "branding")]
// ---- copying files straight from one machine to the other (2026-10-15, HANDOVER §144) ----
[JsonDerivedType(typeof(BridgeAskMsg), "bridge_ask")]
[JsonDerivedType(typeof(BridgeKeyMsg), "bridge_key")]
[JsonDerivedType(typeof(BridgeEndMsg), "bridge_end")]
[JsonDerivedType(typeof(BridgeProgressMsg), "bridge_progress")]
[JsonDerivedType(typeof(BridgeReadyMsg), "bridge_ready")]
[JsonDerivedType(typeof(BridgeAckMsg), "bridge_ack")]
[JsonDerivedType(typeof(UpdateOfferMsg), "update_offer")]
[JsonDerivedType(typeof(UpdateReportMsg), "update_report")]
public abstract class Msg { }

/// <summary>
/// Server -> a machine's agent and tray app: the company's look as it is NOW (2026-10-07, roadmap 4.6). Sent when
/// a machine connects saying a different <see cref="HelloMsg.BrandHash"/>, and to every connected machine the
/// moment the company saves its branding. Only to a link that said <see cref="DeviceCan.Branding"/>.
/// <see cref="BrandLook"/> is all that travels — never the server address or the company key, so this message
/// cannot point a machine anywhere else. <see cref="BrandRefresh"/> is the one place it is checked and kept.
/// </summary>
/// <summary>
/// One file of a release (2026-10-16, auto-update, docs/NEXT-SESSION-AUTOUPDATE.md): which version, where on the
/// machine's own server to fetch it, and its size and SHA-256. The hash only spots a broken download — it is the
/// SIGNATURE that is trusted (<see cref="UpdateTrust"/>), because whoever controls the server controls this too.
/// </summary>
public sealed class UpdateRelease
{
    public string Version { get; set; } = "";
    /// <summary>A path on the server the app already talks to, e.g. <c>/update/client.exe</c> — never another host.</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// Server → agent: a newer release of the machine's software is ready for it (auto-update, stage C). Only ever sent to an
/// agent that said <see cref="DeviceCan.Update"/>, a few machines a minute, in the rings the platform admins have opened.
/// The file is the packed customer app — the tray app with the host, the agent and the scripts inside it.
/// </summary>
public sealed class UpdateOfferMsg : Msg { public UpdateRelease Release { get; set; } = new(); }

/// <summary>Agent → server: how an offered update is going (<see cref="UpdateSteps"/>), kept as the machine's history.</summary>
public sealed class UpdateReportMsg : Msg
{
    public string Version { get; set; } = "";
    public string Step { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>The steps an update reports, in the order they happen. <see cref="Offered"/> is the server's own.</summary>
public static class UpdateSteps
{
    public const string Offered = "offered";
    public const string Downloading = "downloading";
    /// <summary>Downloaded and every file checked; waiting for nobody to be connected.</summary>
    public const string Ready = "ready";
    public const string Installing = "installing";
    /// <summary>The new agent is running and connected — said BY the new agent.</summary>
    public const string Installed = "installed";
    /// <summary>The new agent did not come back in time; the old files were put back.</summary>
    public const string RolledBack = "rolled-back";
    /// <summary>A file was not ours, not newer, or not what was offered: nothing was installed, and this version never will be.</summary>
    public const string Refused = "refused";
    /// <summary>Something went wrong that may go right later (the download, the disk): tried again later.</summary>
    public const string Failed = "failed";

    /// <summary>The steps after which this machine is never offered this version again.</summary>
    public static bool GivesUp(string step) => step is RolledBack or Refused;

    public static string Words(string step) => step switch
    {
        Offered => "Offered",
        Downloading => "Downloading",
        Ready => "Downloaded, waiting until nobody is connected",
        Installing => "Installing",
        Installed => "Updated",
        RolledBack => "Rolled back",
        Refused => "Refused",
        Failed => "Failed, will try again",
        _ => step
    };
}

public sealed class BrandingMsg : Msg
{
    public BrandLook Look { get; set; } = new();
    /// <summary><see cref="BrandRefresh.Hash"/> of the look and the logo; what the machine says in its next hello.</summary>
    public string Hash { get; set; } = "";
    /// <summary>The logo's bytes (PNG or JPEG), or null when the company has none.</summary>
    public byte[]? Logo { get; set; }
}

/// <summary>Host -> viewers: this host seals (2026-10-07). A viewer that can answers with <see cref="E2eHelloMsg"/>.</summary>
public sealed class E2eReadyMsg : Msg { }

/// <summary>Viewer -> host: my ephemeral ECDH P-256 key (65 bytes, uncompressed point, base64). Sent on joining and
/// on every <see cref="E2eReadyMsg"/>. The hub counts which viewer sockets sent one (<see cref="E2eModeMsg"/>).</summary>
public sealed class E2eHelloMsg : Msg { public string Pub { get; set; } = ""; }

/// <summary>
/// Host -> viewers: the session key, for the viewer whose key is <see cref="For"/>. Everybody receives every one;
/// each takes its own. <see cref="Wrapped"/> is K sealed with AES-GCM under HKDF-SHA256 of ECDH(host, viewer);
/// <see cref="Signature"/> is the machine's identity key (ECDSA P-256) over the handshake (<c>SessionSeal.Signed</c>),
/// empty when the host has no identity to sign with (a host running as the person, or a browser).
/// </summary>
public sealed class E2eKeyMsg : Msg
{
    public string For { get; set; } = "";
    public string HostPub { get; set; } = "";
    /// <summary>This viewer's sender number in every nonce it makes (the host is 0).</summary>
    public uint SenderId { get; set; }
    public string Nonce { get; set; } = "";
    public string Wrapped { get; set; } = "";
    public string IdentityPub { get; set; } = "";
    public string Signature { get; set; } = "";
}

/// <summary>Hub -> host: seal content (every viewer can open it) or not (one cannot — an old console). Clear, so a
/// server could force "not" — which every new console then SHOWS (docs/NEXT-SESSION-E2E.md, "Mode").</summary>
public sealed class E2eModeMsg : Msg { public bool Sealed { get; set; } }

/// <summary>
/// Hub -> host: send a complete picture. The hub had to drop a video frame on its way to a viewer whose
/// link could not keep up, and an H.264 picture is a chain — that viewer's is broken until the next key
/// frame. Key frames used to come every 24 frames whatever happened; since 2026-10-15 they come a
/// minute apart (H264Encoder.KeyFrameSeconds), so the hub asks, at most once a second
/// (<see cref="BinaryRelay.LossNeedsKeyFrame"/>). Clear, because only the hub knows a frame was dropped,
/// and it carries nothing. An older host ignores it.
/// </summary>
public sealed class KeyFrameRequestMsg : Msg { }

/// <summary>Console -> hub: the ticket's line for a script run, sent beside the sealed <see cref="ScriptRunMsg"/> —
/// the hub cannot read the script any more, and Clint's audit trail names it. Never passed to the host.</summary>
public sealed class ScriptAuditMsg : Msg
{
    public string Language { get; set; } = "";
    public string FirstLine { get; set; } = "";
}

/// <summary>
/// Server → console: this person has been signed out — removed, their password or boxes changed,
/// their two-factor reset, or their sign-in left unused too long. The server closes the socket
/// straight after; the console goes back to its sign-in window rather than dialling again with a
/// token that will never work.
/// </summary>
public sealed class SignedOutMsg : Msg { public string Reason { get; set; } = ""; }

// ---------------------------------------------------------------------------
// Ticketing
// ---------------------------------------------------------------------------
public sealed class TicketSummary
{
    public int Id { get; set; }
    public int Number { get; set; }
    public string Subject { get; set; } = "";
    public int Status { get; set; }      // 0 Open, 1 InProgress, 2 OnHold, 3 Resolved, 4 Closed
    public int Priority { get; set; }    // 0 Low, 1 Normal, 2 High, 3 Urgent
    /// <summary>Everybody on it, joined by ", " — "Jason, Ryan" — or empty for nobody (2026-09-28).</summary>
    public string AssignedTo { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int Category { get; set; }        // 0 Remote support, 1 Walk in, 2 Workshop, 3 Call out, 4 General
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public DateTime? AppointmentUtc { get; set; }
    /// <summary>The workflow stage in words, or empty for a ticket with none.</summary>
    public string StageLabel { get; set; } = "";
    /// <summary>A recurring task's ticket: the day it was due (the company's date), and whether that has passed with it still open.</summary>
    public DateTime? DueOn { get; set; }
    /// <summary>When the job is due (2026-10-26, HANDOVER §165): a day, or null. <see cref="Overdue"/> is now about this — any ticket.</summary>
    public DateTime? DueDate { get; set; }
    /// <summary>The newest entry on it is the customer's emailed reply — said in the list as "Customer replied" until somebody answers.</summary>
    public bool CustomerReplied { get; set; }
    public bool Overdue { get; set; }

    public static readonly string[] StatusNames = { "Open", "In progress", "On hold", "Resolved", "Closed" };
    public static readonly string[] PriorityNames = { "Low", "Normal", "High", "Urgent" };
    /// <summary>
    /// Category names BY VALUE — the index is the <c>TicketCategory</c> number stored in the
    /// database, so nothing in here may be reordered. Use <see cref="CategoryOrder"/> to draw a
    /// picker.
    /// </summary>
    public static readonly string[] CategoryNames = { "Remote support", "Walk in", "Workshop", "Call out", "General", "Recurring task" };

    /// <summary>
    /// The order a person should be OFFERED the categories, as values rather than positions.
    ///
    /// General leads because it is the default. It is numbered 4 rather than 0 because 0 has
    /// meant Remote support in every ticket row ever written, and renumbering the enum would
    /// silently retype them all. That is why display order and stored value have come apart here:
    /// a picker walks THIS array and carries the value it finds, never its own index.
    /// </summary>
    public static readonly int[] CategoryOrder = { 4, 0, 1, 2, 3, 5 };

    /// <summary>
    /// Each category's own colour, by VALUE (2026-10-09) — Clint: the wrong category had been chosen "because it is not
    /// pronounced enough". One list, read by the portal's New ticket and the console's, so a Workshop job is orange in
    /// both. <c>Fill</c> carries white text (every one is 5:1 or better against white) and is the words and border on a
    /// light ground; <c>OnDark</c> is the words and border on a dark one.
    /// General slate, Remote support blue, Walk in green, Workshop orange, Call out purple, Recurring task teal.
    /// </summary>
    public static readonly (string Fill, string OnDark)[] CategoryColors =
    {
        ("#1D4ED8", "#60A5FA"),   // 0 Remote support — blue
        ("#15803D", "#4ADE80"),   // 1 Walk in — green
        ("#C2410C", "#FB923C"),   // 2 Workshop — orange
        ("#7E22CE", "#C084FC"),   // 3 Call out — purple
        ("#475569", "#94A3B8"),   // 4 General — slate
        ("#0F766E", "#2DD4BF"),   // 5 Recurring task — teal
    };

    public string StatusName => Status >= 0 && Status < StatusNames.Length ? StatusNames[Status] : "Open";
    public string PriorityName => Priority >= 0 && Priority < PriorityNames.Length ? PriorityNames[Priority] : "Normal";
    public string CategoryName => Category >= 0 && Category < CategoryNames.Length ? CategoryNames[Category] : "General";
}

/// <summary>Job type. Values match the server's TicketCategory exactly.</summary>
public enum TicketCategoryKind { RemoteSupport = 0, WalkIn = 1, Workshop = 2, CallOut = 3, General = 4, Recurring = 5 }

/// <summary>A customer record, used by the type-ahead when raising a ticket.</summary>
public sealed class ClientInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public string Display => string.Join(" · ", new[] { Name, ContactName, Phone }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public override string ToString() => Display;

    /// <summary>
    /// The people at this company, so the console can offer "Bob at Zenith" rather than only
    /// "Zenith". Carried on the client rather than fetched separately because picking an owner is
    /// ONE decision — a dialog that has to ask the server again after the company is chosen makes
    /// the technician wait in the middle of it.
    ///
    /// Not to be confused with <see cref="ContactName"/>, which is the single free-text name that
    /// has always sat on a Client. That one is a label; these are rows that machines link to.
    /// </summary>
    public List<ContactInfo> Contacts { get; set; } = new();
}

/// <summary>One person at a customer, for the console's owner picker.</summary>
public sealed class ContactInfo
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class TicketNoteInfo
{
    public string Author { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsSystem { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>The workflow stage it was written in, as words — the heading it is grouped under.</summary>
    public string StageLabel { get; set; } = "";
    public bool IsWorkDone { get; set; }
    /// <summary>A step's form, filled in ("Inspection findings", "Work done"), or empty for a comment.</summary>
    public string FormTitle { get; set; } = "";
    /// <summary>Where this step moved the job, in words, or empty.</summary>
    public string MovedToLabel { get; set; } = "";
    /// <summary>For a Work done entry, what belongs on the invoice — the work, without the time and
    /// amount recorded under it. Worked out on the server (<c>TicketService.WorkDoneText</c>) so the
    /// console and the portal copy the same words.</summary>
    public string WorkDoneText { get; set; } = "";
    /// <summary>The note's id, for "Email to the client…" (2026-10-09). 0 from an older server.</summary>
    public long Id { get; set; }
    /// <summary>A comment somebody typed, which may be emailed to the client — not an automatic line, a step, or the customer's own reply.</summary>
    public bool CanEmail { get; set; }
    /// <summary>The customer's own words, from an emailed reply.</summary>
    public bool FromCustomer { get; set; }
    /// <summary>Each time it was emailed to the client, in words: "Emailed to jane@acme.co.za by Sue Tech, 9 Oct 14:02",
    /// or why it did not go. The server's ClientUpdateService.Marker, so the portal and the console say the same.</summary>
    public List<string> Emailed { get; set; } = new();
    /// <summary>One of those did not go.</summary>
    public bool EmailFailed { get; set; }
    /// <summary>Its files and photos (2026-10-13), each opened in the browser at /portal/attachment/{id}.</summary>
    public List<AttachmentInfo> Attachments { get; set; } = new();
    /// <summary>This step was gone back over (2026-10-13): who, when, and that what it recorded no longer counts. Empty otherwise.</summary>
    public string Undone { get; set; } = "";
    /// <summary>This entry IS a "Back to …".</summary>
    public bool WentBack { get; set; }
}

/// <summary>A file or photo on a comment (2026-10-13).</summary>
public sealed class AttachmentInfo
{
    public long Id { get; set; }
    /// <summary>Its portal address is /portal/f/{Token} (2026-10-23, HANDOVER §159); empty from an older server.</summary>
    public string Token { get; set; } = "";
    /// <summary>What it was called when it was picked.</summary>
    public string Name { get; set; } = "";
    /// <summary>"1.2 MB", in words.</summary>
    public string Size { get; set; } = "";
    public bool IsImage { get; set; }
}

/// <summary>
/// Emailing comments to the client (2026-10-09), as the server answers it for one ticket and one person:
/// whether it is set up, and if not why (in words for them), the address ticked by default, and the
/// others. Another address may always be typed in; the server checks whatever is sent.
/// </summary>
public sealed class ClientUpdateInfo
{
    public bool Ready { get; set; }
    public string WhyNot { get; set; } = "";
    public string Default { get; set; } = "";
    public List<ClientAddressInfo> Choices { get; set; } = new();
    /// <summary>The support address it goes from, and the client's reply comes back to.</summary>
    public string From { get; set; } = "";
}

public sealed class ClientAddressInfo
{
    public string Address { get; set; } = "";
    /// <summary>"Jane Smith &lt;jane@acme.co.za&gt;", or the address alone.</summary>
    public string Label { get; set; } = "";
    public override string ToString() => Label;
}

// ---- the workflow, as the console draws it (stage 3.7) ----
//
// Everything below is an ANSWER from the server: which stage, which buttons, which fields. The
// console never works out a route for itself — Services/Workflow.cs on the server is the one
// definition, and the portal draws the same answer. Two copies would drift.

/// <summary>One button the person looking may press now, and what it needs typed in first.</summary>
public sealed class StageActionInfo
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Field names: Minutes, Amount, Reference, Reason, Repairable. SupplierEmail never
    /// reaches the console — Request quote is the office's, in the portal.</summary>
    public List<string> Fields { get; set; } = new();
    /// <summary>One of the technician's outcomes, drawn after "Repairable or not".</summary>
    public bool IsDecision { get; set; }
    /// <summary>"yes", "no" or "both": which answer to "Is it repairable?" this outcome belongs to.</summary>
    public string Repairable { get; set; } = "both";
    public bool Closes { get; set; }
    /// <summary>Pressing it would finish the job, which an open part of it stops (2026-10-09) — the server's
    /// TicketService.ClosesJob, so the console's pop-up asks exactly what the server will.</summary>
    public bool ClosesJob { get; set; }
    public bool IsDanger { get; set; }
    /// <summary>What to read before pressing it — Back's "the invoice has gone out" (2026-10-13). Shown above its form.</summary>
    public string Warning { get; set; } = "";
}

public sealed class QuoteLineInfo
{
    public long Id { get; set; }
    public string ItemName { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public bool HasPhoto { get; set; }
    /// <summary>How many photos it has (2026-10-06). <see cref="HasPhoto"/> stays for older consoles.</summary>
    public int PhotoCount { get; set; }
}

/// <summary>The machine a quote is for. Every part optional.</summary>
public sealed class QuoteMachineInfo
{
    public string Type { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    /// <summary>Not saved yet: these came from the workshop booking.</summary>
    public bool FromBooking { get; set; }
}

public sealed class CatalogItemInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string LastPartNumber { get; set; } = "";
    public override string ToString() => LastPartNumber.Length > 0 ? $"{Name}  ·  {LastPartNumber}" : Name;
}

/// <summary>A label and a value the route has recorded, e.g. "Time spent" / "1 h 15 min".</summary>
public sealed class FactInfo
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A ticket offered as a choice — "Link to parent ticket" lists the client's open ones.</summary>
public sealed class TicketChoiceInfo
{
    public int Id { get; set; }
    public int Number { get; set; }
    public string Subject { get; set; } = "";
    /// <summary>Where it has got to — filled in for the machines booked in together, not for a parent choice.</summary>
    public string StageLabel { get; set; } = "";
    /// <summary>Not closed — filled in for a parent and its children (2026-10-09).</summary>
    public bool Open { get; set; }
    public override string ToString() => $"#{Number} — {Subject}";
}

/// <summary>A further machine on one job (2026-10-02), as the console draws it.</summary>
public sealed class TicketMachineInfo
{
    public int Id { get; set; }
    public string Purpose { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Accessories { get; set; } = "";
    public bool PasswordProtected { get; set; }
    /// <summary>Decrypted only for signed-in staff, as the ticket's own is.</summary>
    public string Password { get; set; } = "";
    public int PhotosIn { get; set; }
}

public sealed class TicketDetail
{
    public TicketSummary Ticket { get; set; } = new();
    public string Description { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Source { get; set; } = "";
    public string OpenedBy { get; set; } = "";
    // call out
    public string SiteAddress { get; set; } = "";
    // workshop
    public string AssetMake { get; set; } = "";
    public string AssetModel { get; set; } = "";
    public string AssetSerial { get; set; } = "";
    public string Accessories { get; set; } = "";
    public bool DevicePasswordProtected { get; set; }
    /// <summary>Decrypted only for signed-in staff; never stored in plain text.</summary>
    public string DevicePassword { get; set; } = "";
    public List<TicketNoteInfo> Notes { get; set; } = new();
    /// <summary>The recurring task this ticket came from (2026-10-07), or null.</summary>
    public RecurringInfo? Recurring { get; set; }

    // ---- the workflow (stage 3.7). Empty Stage means this ticket has none. ----
    public string Stage { get; set; } = "";
    public string StageLabel { get; set; } = "";
    /// <summary>The stage before this one, for the crumb: Workshop › previous › current.</summary>
    public string PreviousStageLabel { get; set; } = "";
    /// <summary>"With Sue Tech", "In the billing queue", "Waiting for a supervisor".</summary>
    public string WithWhom { get; set; } = "";
    public bool IsClosed { get; set; }
    public List<FactInfo> Facts { get; set; } = new();
    /// <summary>What "Link to parent ticket" may choose from: the same client's open tickets.</summary>
    public List<TicketChoiceInfo> ParentChoices { get; set; } = new();
    /// <summary>The other machines booked in with this one (2026-09-30), each with where it has got to.</summary>
    public List<TicketChoiceInfo> BookedWith { get; set; } = new();
    /// <summary>The job this ticket is filed under (3.6.7), or null — shown, and undone with "Not part of it" (2026-10-09).</summary>
    public TicketChoiceInfo? Parent { get; set; }
    /// <summary>The tickets filed under this one, each with where it has got to and whether it is still open.
    /// An open one stops this job closing (2026-10-09).</summary>
    public List<TicketChoiceInfo> Children { get; set; } = new();
    /// <summary>Pictures of the customer's screen that came with their support requests (2026-10-09), by photo id —
    /// each opens in the portal at /portal/photo/{id}.</summary>
    public List<long> Screenshots { get; set; } = new();
    /// <summary>The same pictures' portal addresses, /portal/p/{token}, in the same order (HANDOVER §159); empty from an older server.</summary>
    public List<string> ScreenshotTokens { get; set; } = new();
    /// <summary>The client this ticket is for, by id (0 for none) — for "Link to a client…".</summary>
    public int ClientId { get; set; }
    public List<StageActionInfo> Actions { get; set; } = new();
    public bool CanAddWorkDone { get; set; }
    public bool MayEditLines { get; set; }
    /// <summary>Whether the quote section belongs on screen at all for this job.</summary>
    public bool ShowQuote { get; set; }
    public List<QuoteLineInfo> QuoteLines { get; set; } = new();
    /// <summary>The machine the quote is for (2026-09-29): all empty when it is not for one.</summary>
    public QuoteMachineInfo QuoteMachine { get; set; } = new();
    /// <summary>The kinds of machine the Type box offers, from the server.</summary>
    public List<string> MachineTypes { get; set; } = new();
    public int PhotosIn { get; set; }
    public int PhotosOut { get; set; }
    public int PhotosRequired { get; set; } = 3;
    /// <summary>Photos out needed before the machines leave: three of EACH machine on the job (2026-10-02).</summary>
    public int PhotosOutRequired { get; set; } = 3;
    /// <summary>The further machines on THIS job (2026-10-02) — a data transfer's old and new. The
    /// ticket's own Asset* fields are machine 1; these are 2, 3…. Empty for a job with one machine.</summary>
    public List<TicketMachineInfo> Machines { get; set; } = new();
    /// <summary>Who asked THIS person something on this ticket and is waiting for a reply.</summary>
    public List<string> AskedBy { get; set; } = new();
    /// <summary>Customers whose emailed reply on this ticket this person has not seen yet (2026-10-09) —
    /// flagged like a mention, and cleared by opening the ticket. Kept out of <see cref="AskedBy"/>, which
    /// an older console words as "asked you something".</summary>
    public List<string> RepliedBy { get; set; } = new();
    /// <summary>Whether comments may be emailed to the client, and to whom (2026-10-09). Null from an older server.</summary>
    public ClientUpdateInfo? ClientUpdate { get; set; }
    /// <summary>Everybody who can be @mentioned.</summary>
    public List<string> People { get; set; } = new();
    /// <summary>Those of them on this ticket — assigned, or did the work — offered first when @ is typed
    /// (2026-10-21, <see cref="MentionPick"/>). Empty from an older server: everybody is then one list.</summary>
    public List<string> OnTicket { get; set; } = new();
    /// <summary>The bench's pool: nobody has it and this person may take it.</summary>
    public bool CanClaim { get; set; }
    /// <summary>
    /// May put anybody on this ticket or take anybody off. Without it a technician may only add
    /// themselves (a bench job) and, on a job they are on, add colleagues and take themselves off.
    /// The server enforces that either way; this only decides which boxes the console lets them tick.
    /// </summary>
    public bool CanAssignToAnyone { get; set; }
    /// <summary>May put themselves on this job beside whoever is there (a technician, on the bench).</summary>
    public bool CanJoin { get; set; }
    /// <summary>May add colleagues to a job they are on, and take themselves off it.</summary>
    public bool CanPassOn { get; set; }
    /// <summary>The knowledge base, as this ticket shows it (3.8.5). Null from an older server.</summary>
    public KnowledgeInfo? Knowledge { get; set; }
}

/// <summary>
/// What a ticket says about the knowledge base (3.8.5): how many articles its client has, the ones
/// written up from it, and whether this person is offered "Add to knowledge base". The server decides
/// all three; the console draws them and opens the portal for the rest.
/// </summary>
public sealed class KnowledgeInfo
{
    public int ClientId { get; set; }
    /// <summary>The client's number in the company, what the portal's addresses name (HANDOVER §159); 0 from an older server.</summary>
    public int ClientNumber { get; set; }
    public string ClientName { get; set; } = "";
    public int ClientCount { get; set; }
    public List<KnowledgeRefInfo> FromTicket { get; set; } = new();
    public bool CanAdd { get; set; }
}

public sealed class KnowledgeRefInfo
{
    public int Id { get; set; }
    /// <summary>The article's number in the company: /portal/a/{Number} (HANDOVER §159); 0 from an older server.</summary>
    public int Number { get; set; }
    public string Title { get; set; } = "";
}

/// <summary>Console → server: what does the knowledge base hold for the client this machine belongs to?</summary>
public sealed class KnowledgeCountRequestMsg : Msg { public string DeviceId { get; set; } = ""; }

/// <summary>
/// Server → console: the answer, so a session window can say "Knowledge base for Acme (3)" the moment
/// it connects (3.8.5). ClientId 0: the machine belongs to no client.
/// </summary>
public sealed class KnowledgeCountMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public int ClientId { get; set; }
    /// <summary>The client's number in the company (HANDOVER §159); 0 from an older server.</summary>
    public int ClientNumber { get; set; }
    public string ClientName { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// "Open the portal in my browser, as me." Asked over the console's already-authenticated
/// socket, which is the point: the server knows who is asking without anybody typing a password
/// a second time.
/// </summary>
public sealed class PortalLinkRequestMsg : Msg
{
    /// <summary>Where in the portal to land, e.g. "/portal/tickets". The server decides what is
    /// allowed; a path it does not recognise is replaced rather than followed.</summary>
    public string Path { get; set; } = "";
}

/// <summary>
/// A URL the console hands to the browser. It carries a SINGLE-USE token that dies the moment it
/// is opened and expires in under a minute regardless.
///
/// That is what stops it being a password in the browser history. The URL stays in history, as
/// every URL does; what is in it is already spent by the time the page has finished loading.
/// </summary>
/// <summary>
/// How many tickets have an @mention waiting for THIS technician's reply (stage 3.7). Sent when the
/// console signs in and whenever it changes — somebody asks them something, or they answer. The
/// console shows it on its Tickets button; the portal has its own badge from the same data.
/// </summary>
public sealed class MentionCountMsg : Msg { public int Count { get; set; } }

/// <summary>
/// The billing queue, to the people told when a job lands in it (2026-10-05): every office
/// administrator, or the supervisors and owner of a company without one. <see cref="Count"/> is how
/// many jobs are waiting for the office to act — sent at sign-in and whenever it changes.
/// <see cref="Arrived"/> is set when a job has just come in and somebody else put it there: the
/// console pops it up. An older console ignores the whole message.
/// </summary>
public sealed class BillingQueueMsg : Msg
{
    public int Count { get; set; }
    public BillingArrivalInfo? Arrived { get; set; }
}

public sealed class BillingArrivalInfo
{
    public int TicketId { get; set; }
    public int Number { get; set; }
    public string Subject { get; set; } = "";
    /// <summary>The stage it arrived at, as people read it: "To be invoiced".</summary>
    public string Stage { get; set; } = "";
    public string By { get; set; } = "";
}

// ---- office chat (stage 3.8.1, 2026-09-30): people at the office, never a customer ----

/// <summary>One office chat message. <see cref="To"/> is a display name, or empty for Everyone.</summary>
public sealed class StaffChatInfo
{
    public long Id { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentUtc { get; set; }
}

/// <summary>One conversation as a person sees it: with somebody, or "" for Everyone.</summary>
public sealed class StaffChatThreadInfo
{
    public string With { get; set; } = "";
    public int Unread { get; set; }
    public StaffChatInfo? Last { get; set; }
}

/// <summary>Console → server: say something to one person, or to Everyone (To empty).</summary>
public sealed class StaffChatSendMsg : Msg { public string To { get; set; } = ""; public string Text { get; set; } = ""; }

/// <summary>
/// Server → console: a message this person is part of — to them, from them, or to Everyone. Error
/// is set only on the sender's copy of a refused send. MentionsMe: an Everyone message that names
/// them with @, which is what pops the window open (a direct message always does).
/// </summary>
public sealed class StaffChatMsg : Msg
{
    public StaffChatInfo? Message { get; set; }
    public bool MentionsMe { get; set; }
    public string Error { get; set; } = "";
}

public sealed class StaffChatHistoryRequestMsg : Msg { public string With { get; set; } = ""; }
public sealed class StaffChatHistoryMsg : Msg { public string With { get; set; } = ""; public List<StaffChatInfo> Messages { get; set; } = new(); }
/// <summary>Console → server: everything in this conversation up to this message has been seen.</summary>
public sealed class StaffChatReadMsg : Msg { public string With { get; set; } = ""; public long UpToId { get; set; } }
public sealed class StaffChatOverviewRequestMsg : Msg { }
/// <summary>Everybody this person can chat with, and every conversation with its unread count.</summary>
public sealed class StaffChatOverviewMsg : Msg
{
    public List<string> People { get; set; } = new();
    public List<StaffChatThreadInfo> Threads { get; set; } = new();
    public int TotalUnread { get; set; }
}

public sealed class PortalLinkMsg : Msg
{
    /// <summary>
    /// A path with the token on it, e.g. "/portal/enter?t=…", RELATIVE on purpose.
    ///
    /// The console resolves it against the server it is already talking to, rather than the server
    /// naming itself. A platform whose PublicUrl is unset or stale — which is every development
    /// machine and any production box the day somebody changes a domain — would otherwise hand
    /// out links to somewhere the browser cannot reach. The address the console connected to is
    /// the one address known to work.
    /// </summary>
    public string Url { get; set; } = "";
    /// <summary>Empty when the link is good; otherwise why there is no link.</summary>
    public string Error { get; set; } = "";
}

/// <summary>Sort: a <see cref="TicketSort"/> key (2026-10-13). Empty — every older console — is the order it always was.</summary>
public sealed class TicketListRequestMsg : Msg { public string Filter { get; set; } = "open"; public string Search { get; set; } = ""; public string Sort { get; set; } = ""; }
public sealed class TicketListMsg : Msg { public string Filter { get; set; } = "open"; public string Sort { get; set; } = ""; public List<TicketSummary> Tickets { get; set; } = new(); }
public sealed class TicketOpenMsg : Msg { public int TicketId { get; set; } }
public sealed class TicketDetailMsg : Msg { public TicketDetail Detail { get; set; } = new(); }
public sealed class TicketCreateMsg : Msg
{
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public bool AssignToMe { get; set; } = true;
    /// <summary>Who to give the job to — one name or several joined by ", ". Overrides AssignToMe when set.</summary>
    public string? AssignTo { get; set; }
    public int Category { get; set; }
    /// <summary>Existing client to link to, or 0 to create one from NewClient*.</summary>
    public int ClientId { get; set; }
    public string NewClientName { get; set; } = "";
    public string NewClientPhone { get; set; } = "";
    public string NewClientEmail { get; set; } = "";
    public string NewClientAddress { get; set; } = "";
    /// <summary>
    /// The booking form the workshop photos were uploaded against, so the server can find them and
    /// attach them to the ticket it is about to create. Empty for every other category.
    ///
    /// The photos go up over HTTP before this message is sent — a websocket message carrying three
    /// 5 MB images would block the console's control connection, which is the same socket a call
    /// rings on.
    /// </summary>
    public string PhotoStageId { get; set; } = "";
    // call out
    public string SiteAddress { get; set; } = "";
    public DateTime? AppointmentUtc { get; set; }
    /// <summary>When the job is due (2026-10-26, HANDOVER §165): a day, or null for none — the DAY at midnight marked UTC, never a
    /// local time (one sent with an offset is turned into the server's time and can land on the day before).</summary>
    public DateTime? DueDate { get; set; }
    // workshop
    public string AssetMake { get; set; } = "";
    public string AssetModel { get; set; } = "";
    public string AssetSerial { get; set; } = "";
    public string Accessories { get; set; } = "";
    public bool DevicePasswordProtected { get; set; }
    public string DevicePassword { get; set; } = "";

    // recurring task (2026-10-07): the schedule this sets up instead of one ticket
    /// <summary>Maintenance, Billing, Readings, or a new type, which the company keeps.</summary>
    public string RecurringKind { get; set; } = "";
    /// <summary>0 daily, 1 weekly, 2 monthly.</summary>
    public int Repeats { get; set; }
    public bool WeekdaysOnly { get; set; }
    /// <summary>Weekly: one bit per DayOfWeek, Sunday 1 … Saturday 64.</summary>
    public int Days { get; set; }
    /// <summary>Monthly: 1 to 31, or 0 for the last day.</summary>
    public int MonthDay { get; set; }
}

// ---- recurring tasks (2026-10-07) ----

/// <summary>What a recurring task's ticket says about the schedule it came from.</summary>
public sealed class RecurringInfo
{
    public int TaskId { get; set; }
    /// <summary>The task's number in the company: /portal/r/{TaskNumber} (HANDOVER §159); 0 from an older server.</summary>
    public int TaskNumber { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>"Every Monday and Thursday", "Monthly on the last day".</summary>
    public string Repeats { get; set; } = "";
    /// <summary>"Next: Monday 12 October", or "Cancelled 7 October by Sue Tech: no longer needed".</summary>
    public string Next { get; set; } = "";
    public bool Cancelled { get; set; }
    /// <summary>Whether this person may change or cancel it: the people on it, a supervisor or the owner.</summary>
    public bool MayChange { get; set; }
}

/// <summary>Console → server: the types of recurring task this company has, for New ticket.</summary>
public sealed class RecurringKindsRequestMsg : Msg { }
public sealed class RecurringKindsMsg : Msg
{
    public List<string> Kinds { get; set; } = new();
    /// <summary>Everybody a task may go to — the office too, not only the console's roster: a billing or
    /// readings task is often the office administrator's.</summary>
    public List<string> People { get; set; } = new();
}

/// <summary>Server → console: a recurring task was set up from New ticket. Its first ticket, if it opened
/// today, follows as an ordinary TicketDetailMsg.</summary>
public sealed class RecurringSetMsg : Msg { public int TaskId { get; set; } public string Message { get; set; } = ""; }

/// <summary>Console → server: stop a recurring task opening any more tickets. Answered with a
/// TicketNoticeMsg and the ticket again.</summary>
public sealed class RecurringCancelMsg : Msg
{
    public int TaskId { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>The ticket it was cancelled from, to redraw.</summary>
    public int TicketId { get; set; }
}

// ---- clients ----
public sealed class ClientSearchMsg : Msg
{
    public string Term { get; set; } = "";
    /// <summary>
    /// How many to send back. 12 is the type-ahead default the New ticket dialog wants — enough to
    /// catch a duplicate while somebody types, few enough not to be a list. The owner picker asks
    /// for far more, because somebody who does not know whose stray machine this is has nothing to
    /// type and needs to be able to look.
    /// </summary>
    public int Take { get; set; } = 12;
}
public sealed class ClientListMsg : Msg { public string Term { get; set; } = ""; public List<ClientInfo> Clients { get; set; } = new(); }

/// <summary>
/// Says who a machine belongs to, sent from the console.
///
/// The console half of what the portal's Devices page has always been able to do. Until a machine
/// is linked its tickets never reach a client page, so leaving this to whoever can sign in to the
/// portal as the owner meant the person actually looking at the stray machine could not fix it.
///
/// <see cref="ContactId"/> alone is enough — a person implies their company, and the server fills
/// the client in. Both may be zero, which unlinks the machine.
/// </summary>
public sealed class DeviceLinkMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public int ClientId { get; set; }
    public int ContactId { get; set; }
    /// <summary>
    /// Add this person as a contact at <see cref="ClientId"/> and link the machine to them (2026-10-18) — the name they
    /// gave at a support request, offered by the Link window when nobody on file fits. Ignored with a ContactId. An
    /// existing contact of that name at that client is reused, never doubled (ContactService.CreateAsync).
    /// </summary>
    public string NewContactName { get; set; } = "";
}
/// <summary>
/// The monthly health check, as chosen for ONE machine — from the console's right-click.
///
/// The customer's own tick is the usual way a machine gets it (an SLA client ticks once and every
/// machine they own follows, including machines added later). This is the exception for the odd
/// machine that differs, so it has three values rather than two: follow the customer, on, off.
/// </summary>
public sealed class DeviceHealthCheckMsg : Msg
{
    public string DeviceId { get; set; } = "";
    /// <summary>A <see cref="HealthCheckModes"/> value.</summary>
    public int Mode { get; set; }
}

/// <summary>
/// Server -> the machine's AGENT SERVICE: whether the monthly task should be on this machine.
///
/// Sent when the agent connects and again whenever the answer changes. It is a statement of the
/// state wanted, not a one-off "install now" — so a machine that was switched off while somebody
/// changed it comes back and catches up, and sending it twice does nothing the second time.
///
/// The agent, because only a SYSTEM service can register or remove a SYSTEM scheduled task. The
/// tray app runs as the person, and an attended session's host does too.
/// </summary>
public sealed class HealthCheckWantedMsg : Msg
{
    public bool On { get; set; }
    /// <summary>The IT company's chosen day, 0 for "pick from the install weekday". Used only on a
    /// fresh install — an existing install keeps the day it already has.</summary>
    public int DayOfMonth { get; set; }
}

/// <summary>Agent -> server: what is actually on the machine after it acted, in its own words.</summary>
public sealed class HealthCheckStatusMsg : Msg
{
    public bool Installed { get; set; }
    /// <summary>Plain words for the console and the portal — "installed", "could not install: …",
    /// "another health check is installed here and was left alone".</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// The three choices for a machine's health check, and what they come to. Numbers, because they
/// are stored; do not renumber them — 0 is what every existing machine reads as.
/// </summary>
public static class HealthCheckModes
{
    public const int AsClient = 0;
    public const int On = 1;
    public const int Off = 2;

    public static int Normalise(int mode) => mode is On or Off ? mode : AsClient;

    /// <summary>
    /// Whether a machine should have the monthly task. The ONE place this is decided — the hub,
    /// the portal and every harness ask it.
    ///
    /// The company switch comes first and wins: a company with the health check off, or with no
    /// mailbox to send the reports from, wants it nowhere, whatever a machine is set to. A machine
    /// that follows its customer and has no customer does not get it.
    /// </summary>
    public static bool Wanted(bool companyAvailable, int mode, bool clientTicked) =>
        companyAvailable && Normalise(mode) switch
        {
            On => true,
            Off => false,
            _ => clientTicked
        };

    /// <summary>
    /// Whether a choice from the console's menu should go to the server. A different choice, always.
    /// The SAME choice when the machine has not done what it says - wanted and not installed, or
    /// installed and not wanted - because then choosing it again is the retry (BUGS.md, bug 9).
    ///
    /// It used to be "only a different choice", so after an antivirus blocked an install, clicking
    /// On again did nothing at all, and the only way to retry was Off then On - which runs the
    /// removal first, on a machine whose files were being held. No new control for this: the menu
    /// item that already says what you want is the one that retries it.
    /// </summary>
    public static bool WorthSending(int currentMode, int chosenMode, bool wanted, bool? installed) =>
        Normalise(currentMode) != Normalise(chosenMode) || (installed == true) != wanted;

    /// <summary>
    /// One line saying where the machine stands, for the console's menu and the portal. Decision
    /// first, then what the machine says — "installed is not running" (§41) applied here: the
    /// wanted state is never allowed to stand in for what the machine reported.
    /// </summary>
    public static string Describe(bool companyAvailable, bool wanted, bool? installed, string note, bool agentOnline)
    {
        if (!companyAvailable)
            return installed == true
                ? "Switched off for your company - it will be removed when this machine next connects"
                : "Switched off for your company (portal, Health check settings)";
        if (wanted)
        {
            if (installed == true) return "On - installed on the machine";
            if (!string.IsNullOrWhiteSpace(note) && installed == false) return "On - " + note.Trim();
            return agentOnline ? "On - waiting for the machine to install it"
                               : "On - will install when the machine's background service connects";
        }
        if (installed == true)
            return agentOnline ? "Off - waiting for the machine to remove it"
                               : "Off - will be removed when the machine's background service connects";
        if (!string.IsNullOrWhiteSpace(note) && installed == false && note.Contains("left alone")) return "Off - " + note.Trim();
        return "Off";
    }

    /// <summary>
    /// The "follow the customer" choice, saying what following them comes to today — so nobody has
    /// to go and look up whether Zenith has an SLA before choosing. An unlinked machine has nobody
    /// to follow, and says so rather than naming a blank.
    /// </summary>
    public static string AsClientLabel(int clientId, string clientName, bool clientTicked) =>
        clientId > 0 && !string.IsNullOrWhiteSpace(clientName)
            ? $"As {clientName.Trim()} ({(clientTicked ? "on" : "off")})"
            : "As its customer (none linked, so off)";
}

public sealed class TicketUpdateMsg : Msg
{
    public int TicketId { get; set; }
    public int? Status { get; set; }
    public int? Priority { get; set; }
    /// <summary>The WHOLE list of people wanted on it afterwards, joined by ", "; null leaves it alone.</summary>
    public string? AssignedTo { get; set; }
    public string? Subject { get; set; }
}
public sealed class TicketNoteAddMsg : Msg
{
    public int TicketId { get; set; }
    public string Body { get; set; } = "";
    /// <summary>Also email this comment to the client, at this address (2026-10-09). Empty: internal only —
    /// what every older console sends.</summary>
    public string EmailTo { get; set; } = "";
    /// <summary>Files and photos picked for it (2026-10-13), uploaded first to /api/portal/attachments. With files the
    /// words may be empty.</summary>
    public List<long> AttachmentIds { get; set; } = new();
}
/// <summary>"Email to the client…" on a comment already posted (2026-10-09). Answered by a TicketNoticeMsg and the ticket again.</summary>
public sealed class TicketEmailNoteMsg : Msg { public int TicketId { get; set; } public long NoteId { get; set; } public string To { get; set; } = ""; }
/// <summary>Which client a ticket is for (0 unlinks), and optionally the person there.</summary>
public sealed class TicketSetClientMsg : Msg { public int TicketId { get; set; } public int ClientId { get; set; } public string? ContactName { get; set; } }
/// <summary>Files a ticket under another job, or (0) takes it out of the one it is in — "Not part of it" (2026-10-09).</summary>
public sealed class TicketSetParentMsg : Msg { public int TicketId { get; set; } public int ParentTicketId { get; set; } }
/// <summary>
/// "Edit the ticket" (2026-10-13): its subject, description, category and appointment. A null field is left alone;
/// <see cref="AppointmentGiven"/> says the appointment was sent at all, because null there means "no appointment". The
/// answer is a <see cref="TicketNoticeMsg"/> and the ticket again.
/// </summary>
public sealed class TicketEditMsg : Msg
{
    public int TicketId { get; set; }
    public string? Subject { get; set; }
    public string? Description { get; set; }
    /// <summary>The category's VALUE (<see cref="TicketCategoryKind"/>), never its place in a list.</summary>
    public int? Category { get; set; }
    public bool AppointmentGiven { get; set; }
    public DateTime? AppointmentUtc { get; set; }
    /// <summary>The due date (2026-10-26, HANDOVER §165): a day, given or not — an older console never gives one.</summary>
    public bool DueGiven { get; set; }
    public DateTime? DueDate { get; set; }
}
/// <summary>Broadcast to every technician in the tenant so open ticket lists stay in sync.</summary>
public sealed class TicketChangedMsg : Msg { public TicketSummary Ticket { get; set; } = new(); }

/// <summary>Press a stage button. Minutes and Amount are sent as TYPED ("1:30", "R 1 250,00") and read
/// on the server, by the same parser the portal uses, so the two cannot disagree about a number.</summary>
public sealed class TicketActMsg : Msg
{
    public int TicketId { get; set; }
    public string Action { get; set; } = "";
    public string Minutes { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool? Repairable { get; set; }
    /// <summary>The body of a step's form: what the inspection found, what was done.</summary>
    public string Details { get; set; } = "";
    // A call out going to the workshop: the booking's questions. The password travels as typed, over
    // the same socket as every other ticket field, and is encrypted by the hub before it is stored.
    public string AssetMake { get; set; } = "";
    public string AssetModel { get; set; } = "";
    public string AssetSerial { get; set; } = "";
    public string Accessories { get; set; } = "";
    public string DevicePassword { get; set; } = "";
    /// <summary>Link to parent ticket: the ticket chosen.</summary>
    public int ParentTicketId { get; set; }
    /// <summary>Add another machine: where its photos were staged as they were uploaded.</summary>
    public string PhotoStageId { get; set; } = "";
    /// <summary>Add another machine: on THIS ticket, part of the same job (a data transfer), rather
    /// than a ticket of its own.</summary>
    public bool SameJob { get; set; }
    /// <summary>Reschedule: the new appointment, in UTC (the console's own clock, converted).</summary>
    public DateTime? AppointmentUtc { get; set; }
    /// <summary>Comments that may be left empty (Damage report needed, 2026-10-13).</summary>
    public string Comments { get; set; } = "";
    /// <summary>Files picked for the step — the damage report — uploaded first to /api/portal/attachments.</summary>
    public List<long> AttachmentIds { get; set; } = new();
    /// <summary>Email the damage report to the client at this address once it is recorded. Empty: nobody.</summary>
    public string EmailTo { get; set; } = "";
}

public sealed class TicketWorkDoneMsg : Msg { public int TicketId { get; set; } public string Body { get; set; } = ""; }

/// <summary>What happened to something the console asked of a ticket: a refusal in words, or empty.</summary>
public sealed class TicketNoticeMsg : Msg { public int TicketId { get; set; } public string Error { get; set; } = ""; public string Message { get; set; } = ""; }

/// <summary>The Item box's dropdown: an empty term is the whole list; otherwise every typed word starts
/// a word of the item, any order.</summary>
public sealed class ItemSuggestMsg : Msg { public string Term { get; set; } = ""; }

/// <summary>Which machine a quote is for; all empty says it is not for one.</summary>
public sealed class QuoteMachineMsg : Msg
{
    public int TicketId { get; set; }
    public string Type { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
}
public sealed class ItemListMsg : Msg { public string Term { get; set; } = ""; public List<CatalogItemInfo> Items { get; set; } = new(); }

/// <summary>Add a quote line: an item from the list (ItemId), or a new name (ItemId 0). A new name that
/// looks like one already on the list comes back as a QuoteSimilarMsg unless ConfirmNew.</summary>
public sealed class QuoteLineAddMsg : Msg
{
    public int TicketId { get; set; }
    public int ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public bool ConfirmNew { get; set; }
    public string PartNumber { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public int Quantity { get; set; } = 1;
    /// <summary>The one photo an older console sends. A newer one sends <see cref="PhotoIds"/>, and
    /// the first of them here as well, so an older server still keeps one.</summary>
    public long? PhotoId { get; set; }
    /// <summary>Every photo of the part (2026-10-06), each uploaded first over HTTP.</summary>
    public List<long> PhotoIds { get; set; } = new();
}
public sealed class QuoteLineRemoveMsg : Msg { public int TicketId { get; set; } public long LineId { get; set; } }
/// <summary>More photos for an item already on the quote (2026-10-06), each uploaded first over HTTP.</summary>
public sealed class QuoteLinePhotosMsg : Msg
{
    public int TicketId { get; set; }
    public long LineId { get; set; }
    public List<long> PhotoIds { get; set; } = new();
}

/// <summary>"Did you mean" — the typed name and the look-alikes already on the list.</summary>
public sealed class QuoteSimilarMsg : Msg
{
    public int TicketId { get; set; }
    public string Typed { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public long? PhotoId { get; set; }
    public List<long> PhotoIds { get; set; } = new();
    public List<CatalogItemInfo> Similar { get; set; } = new();
}

public enum DeviceRole { Client, Agent, Helper }

public sealed class HelloMsg : Msg
{
    public DeviceRole Role { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Os { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Only for Helper role: token of the session it should host.</summary>
    public string? HostToken { get; set; }
    /// <summary>Multi-tenant platform: identifies which company this device belongs to. Ignored by the single-tenant server.</summary>
    public string? TenantKey { get; set; }
    /// <summary>Hardware summary so technicians can size up a machine at a glance.</summary>
    public SystemSpec? Spec { get; set; }
    /// <summary>
    /// Hashed hardware identity, stable across an uninstall/reinstall that loses device.json.
    /// Empty when the machine will not give one up; matching simply does not happen then.
    /// </summary>
    public string HardwareId { get; set; } = "";
    /// <summary>
    /// What this build can do that an older one cannot, by name (<see cref="DeviceCan"/>). A list
    /// rather than a version number, because the question the server asks is never "how new" but
    /// "may I send you THIS" — and an older device that says nothing is answered "no".
    /// </summary>
    public List<string> Can { get; set; } = new();
    /// <summary>
    /// This machine's network cards and the networks they are on (Wake-on-LAN, 2026-10-06), sent at
    /// every connect because a laptop moves. Null from an older build.
    /// </summary>
    public List<NetworkCard>? Cards { get; set; }
    /// <summary>
    /// The <see cref="BrandingMsg.Hash"/> of the look this machine already has (2026-10-07), or empty for the one it
    /// was installed with. The server sends <see cref="BrandingMsg"/> only when its own differs.
    /// </summary>
    public string BrandHash { get; set; } = "";
    /// <summary>
    /// Who this machine was installed for (2026-10-23, <see cref="GivenPerson"/>): the name and email typed into the installer
    /// window, and that typing's id. Empty from a scripted install or an older build. The server keeps them once per id —
    /// after that, a support request with another name wins.
    /// </summary>
    public string InstalledForId { get; set; } = "";
    public string InstalledForName { get; set; } = "";
    public string InstalledForEmail { get; set; } = "";
}

/// <summary>Names a device may put in <see cref="HelloMsg.Can"/>.</summary>
public static class DeviceCan
{
    /// <summary>
    /// The agent honours <see cref="StartSessionMsg.AskConsent"/>: its host puts the consent prompt
    /// in front of the person and connects nothing until they allow it (2026-10-02). An agent that
    /// does not say so is never handed an attended session, because it would start one WITHOUT
    /// asking.
    /// </summary>
    public const string AskConsent = "ask-consent";

    /// <summary>
    /// This build can send live figures for the console's Processes and Performance windows
    /// (<see cref="WatchRunMsg"/>). Said by the agent and the tray app alike; a machine where neither
    /// says it has those two menu items greyed, with "update the support client" beside them.
    /// </summary>
    public const string Watch = "watch";

    /// <summary>
    /// This tray app asks the person whenever <see cref="StartSessionMsg.AskConsent"/> says to, even when its
    /// company's branding does not require consent (2026-10-06). Somebody from OUTSIDE the company
    /// (roadmap 3.9) is only ever let in after the person at the machine says yes, and an older tray app
    /// would decide that from the branding alone — so it is never handed such a session.
    /// </summary>
    public const string AlwaysAsk = "always-ask";

    /// <summary>
    /// This build can send a Wake-on-LAN wake-up for ANOTHER machine on its network (<see cref="WakeSendMsg"/>,
    /// 2026-10-06). Said by the agent and the tray app alike; an older client is never asked.
    /// </summary>
    public const string Wake = "wake";

    /// <summary>
    /// This build takes the company's current look (<see cref="BrandingMsg"/>, 2026-10-07) and keeps it beside its
    /// installed branding. An older build is never sent it — a logo can be 2 MB, and it would be thrown away.
    /// </summary>
    public const string Branding = "branding";

    /// <summary>
    /// This agent updates its machine itself (2026-10-16, auto-update): it takes <see cref="UpdateOfferMsg"/>, checks
    /// every file with <see cref="UpdateTrust"/>, installs when nobody is connected and rolls back if the new agent does
    /// not come back. A machine whose agent never said it needs one more reinstall by hand to get it.
    /// </summary>
    public const string Update = "update";
}

/// <summary>Headline hardware facts, collected once when a device connects.</summary>
public sealed class SystemSpec
{
    /// <summary>Friendly edition and version, e.g. "Windows 11 Pro 24H2 (26100)".</summary>
    public string Os { get; set; } = "";
    public string Cpu { get; set; } = "";
    public int Cores { get; set; }
    public int RamMb { get; set; }
    public int DiskTotalGb { get; set; }
    public int DiskFreeGb { get; set; }

    public string RamText => RamMb <= 0 ? "—" : RamMb >= 1024 ? $"{Math.Round(RamMb / 1024.0)} GB" : $"{RamMb} MB";
    public int DiskUsedPercent => DiskTotalGb <= 0 ? 0 : (int)Math.Round((DiskTotalGb - DiskFreeGb) * 100.0 / DiskTotalGb);
    public string DiskText => DiskTotalGb <= 0 ? "—" : $"{DiskTotalGb - DiskFreeGb}/{DiskTotalGb} GB ({DiskUsedPercent}%)";
    /// <summary>Trims the marketing noise so a CPU fits in a narrow column.</summary>
    public string CpuShort
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Cpu)) return "—";
            var s = Cpu.Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Replace("Processor", "");
            var at = s.IndexOf(" @", StringComparison.Ordinal);
            if (at > 0) s = s[..at];
            return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}

public sealed class RequestCallMsg : Msg
{
    public string ContactName { get; set; } = "";
    /// <summary>Their email, optional — filled in from the last request or the install (2026-10-23). Kept with the name.</summary>
    public string ContactEmail { get; set; } = "";
    /// <summary>The SUBJECT since 2026-10-09 (<see cref="SupportRequest"/>); an older tray app sends all its words here.</summary>
    public string Issue { get; set; } = "";
    /// <summary>What is wrong in their own words — optional; the ticket's description (2026-10-09).</summary>
    public string Details { get; set; } = "";
    /// <summary>A picture of their screen, JPEG, taken before they filled the request in — or null (2026-10-09). Kept on
    /// the ticket; never passed on to consoles with the call.</summary>
    public byte[]? Screenshot { get; set; }
}
public sealed class CancelCallMsg : Msg { public string? CallId { get; set; } }
public sealed class AcceptCallMsg : Msg { public string CallId { get; set; } = ""; }
public sealed class JoinSessionMsg : Msg { public string SessionId { get; set; } = ""; }
public sealed class ConnectDeviceMsg : Msg
{
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// Why this technician is connecting. Becomes the ticket's description, exactly as a
    /// customer's own reason has since 3.5.7.
    ///
    /// It travels WITH the request rather than arriving afterwards, and that is the whole design:
    /// the ticket is written on the server at the moment the session starts, so a reason sent in a
    /// second message would be a race against the ticket it is supposed to describe.
    ///
    /// Required for an unattended connection and refused without it. A session started from a
    /// CALL leaves this empty — the customer already said what was wrong, and asking the
    /// technician to say it again would replace their words with a guess.
    /// </summary>
    public string Reason { get; set; } = "";

    /// <summary>
    /// The machine's unattended password, when its person has set one (2026-10-02). Checked on the
    /// SERVER against a one-way hash and never stored or passed on. Empty is "I do not know it",
    /// which is refused without counting as a wrong guess, so a console reconnecting through a
    /// restart cannot lock a machine out by asking without one.
    /// </summary>
    public string Password { get; set; } = "";
}
public sealed class EndSessionMsg : Msg { public string SessionId { get; set; } = ""; }
public sealed class PingMsg : Msg { }

public sealed class ChatMsg : Msg
{
    /// <summary>Device this chat belongs to (server fills for device-origin messages).</summary>
    public string? DeviceId { get; set; }
    public string From { get; set; } = "";
    public bool FromTech { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ErrorMsg : Msg
{
    public string Message { get; set; } = "";
    /// <summary>
    /// Set when a connection was refused for want of the machine's unattended password, or for a
    /// wrong one, with the machine it was about — so a console can ask for it again rather than
    /// only show the words. An older console ignores both and shows <see cref="Message"/>, which
    /// is written to make sense on its own.
    /// </summary>
    public bool NeedsPassword { get; set; }
    /// <summary>With <see cref="NeedsPassword"/>: none was given at all — the first ask, not a wrong one.</summary>
    public bool NoPasswordGiven { get; set; }
    /// <summary>
    /// A connection sent with no reason, and the machine has no ticket to carry on (2026-10-09): the console
    /// asks why and connects again. With <see cref="DeviceId"/>. An older console always sends a reason.
    /// </summary>
    public bool NeedsReason { get; set; }
    public string? DeviceId { get; set; }
    /// <summary>
    /// To a MACHINE: it is not let on, and trying again will not change that (2026-10-06, the free
    /// version) — a third computer, or one that was removed. The installer shows <see cref="Message"/>
    /// and installs nothing; a running agent stops dialling.
    /// </summary>
    public bool NotAdmitted { get; set; }
    /// <summary>
    /// To an AGENT, with <see cref="NotAdmitted"/>: the machine was removed from the company's list while it was
    /// connected, so it takes itself off — the service, its files, its health check (2026-10-06, Clint's answer).
    /// A machine that was offline is only refused when it comes back, and stays installed.
    /// </summary>
    public bool Uninstall { get; set; }
}

public enum CallStatus { Ringing, Accepted, Cancelled, Ended }

public sealed class CallInfo
{
    public string CallId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string ContactName { get; set; } = "";
    /// <summary>The request's subject (2026-10-09) — what the alert shows largest.</summary>
    public string Issue { get; set; } = "";
    /// <summary>The rest of what they wrote, or empty.</summary>
    public string Details { get; set; } = "";
    /// <summary>A picture of their screen came with it, and is on the ticket.</summary>
    public bool HasScreenshot { get; set; }
    public CallStatus Status { get; set; }
    public string? AcceptedBy { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Os { get; set; } = "";
    public bool Online { get; set; }
    /// <summary>
    /// A technician can connect to this machine with nobody at it: the agent service is connected
    /// AND the person at the machine has allowed it.
    ///
    /// It used to mean only the first half. Since the tray app grew a switch, a machine can have
    /// a perfectly healthy agent and still be attended-only, which is what the customer asked
    /// for and is what the device list has to say.
    /// </summary>
    public bool Unattended { get; set; }
    /// <summary>The agent service is connected — the capability, with or without permission.</summary>
    public bool AgentOnline { get; set; }
    /// <summary>The permission, as stored by the server, with or without the capability. Both are
    /// carried so the console can tell "they turned it off" from "that machine has no agent".</summary>
    public bool UnattendedEnabled { get; set; }
    /// <summary>
    /// The person at the machine has set an unattended password (2026-10-02): connecting to it
    /// unattended takes that password, from everybody, the owner included. Only whether one is
    /// SET travels here — the hash never leaves the server.
    /// </summary>
    public bool UnattendedPassword { get; set; }
    /// <summary>
    /// The machine came back with a new install and was matched to this record by its hardware alone
    /// (2026-10-07), and nobody in the office has yet confirmed it is the same machine. Until a supervisor
    /// or the owner does, unattended access is held back whatever the tick says: the fingerprint can be
    /// read by anything running on the machine, so a stranger's PC could present it. Attended help is
    /// unaffected — the person there is asked, as on any attended machine.
    /// </summary>
    public bool ReinstallUnconfirmed { get; set; }
    public bool ClientOnline { get; set; } // user-facing app connected
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>
    /// What the agent service and the tray app last said they were (<see cref="AppVersion"/>, 2026-10-16), kept while
    /// the machine is off. Empty for a half that has not connected since the server started recording them. The
    /// machine's version, as a list shows it, is the older of the two: <see cref="AppVersion.Oldest"/>.
    /// </summary>
    public string AgentVersion { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    /// <summary>
    /// Its agent said <see cref="DeviceCan.Update"/> when it last connected (kept while it is off): it will take updates
    /// by itself. False is a machine that needs ONE more reinstall by hand — every machine installed before auto-update.
    /// </summary>
    public bool SelfUpdates { get; set; }
    /// <summary>Processor, memory and disk, so the device list is useful at a glance.</summary>
    public SystemSpec? Spec { get; set; }
    /// <summary>Ticketing client this machine belongs to.</summary>
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    /// <summary>The person whose machine this is, when it is one person's machine rather than a
    /// server or a shared PC. Zero and empty when it hangs off the company directly.</summary>
    public int ContactId { get; set; }
    public string ContactName { get; set; } = "";
    /// <summary>The name the person here last gave when they asked for help (2026-10-18), and when — what they typed,
    /// never checked. Shown only while the machine is linked to nobody (<see cref="OwnerNames"/>).</summary>
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset? RequestedByUtc { get; set; }
    /// <summary>The email given with that name (2026-10-23) — at the install or a support request — or empty. Linking the
    /// machine to a client adds that person as a contact with it.</summary>
    public string RequestedByEmail { get; set; } = "";

    /// <summary>Something connected on this machine can send live figures for Processes and
    /// Performance (<see cref="DeviceCan.Watch"/>). False on a machine still running an older client.</summary>
    public bool CanWatch { get; set; }

    /// <summary>The server knows this machine's network cards and network, so it can try to wake it
    /// (<see cref="WakeRequestMsg"/>). False until it has connected once with a build that reports them.</summary>
    public bool CanWake { get; set; }

    // ---- the monthly health check ----
    // Three facts, carried separately for the same reason as unattended access (§31, §41): what
    // somebody DECIDED, what that decision comes to, and what the MACHINE says is true. A console
    // that drew only the second would say "on" about a machine that has never installed it.

    /// <summary>What was chosen for this machine: a <see cref="HealthCheckModes"/> value — follow
    /// its customer, on, or off.</summary>
    public int HealthCheckMode { get; set; }
    /// <summary>The customer's own tick, so the menu can say what "as the customer" means today.</summary>
    public bool ClientHealthCheck { get; set; }
    /// <summary>Whether the IT company has the health check switched on and its mail finished. Off,
    /// nothing is wanted anywhere, whatever the ticks say.</summary>
    public bool HealthCheckAvailable { get; set; }
    /// <summary>What all of that comes to: the server wants the monthly task on this machine.</summary>
    public bool HealthCheckWanted { get; set; }
    /// <summary>What the machine's agent last REPORTED — null when it never has. The only one of
    /// these that is a fact about the machine rather than about a decision.</summary>
    public bool? HealthCheckInstalled { get; set; }
    /// <summary>The agent's own words about it, e.g. why an install failed.</summary>
    public string HealthCheckNote { get; set; } = "";

    // ---- outside help (roadmap 3.9) ----
    /// <summary>What was chosen for this machine: an <see cref="OutsiderAccess"/> mode — follow the
    /// company, on, or off.</summary>
    public int OutsiderMode { get; set; }
    /// <summary>The company's own switch, so the menu can say what "as the company" means today.</summary>
    public bool OutsiderCompanyOn { get; set; }
    /// <summary>What that comes to: the person at this machine may give somebody outside a code.</summary>
    public bool OutsiderAllowed { get; set; }

    /// <summary>
    /// What the device list puts in its first column: the person, else the company, else the name they
    /// gave when they asked for help, else whoever is signed in at the machine.
    ///
    /// The fallbacks are the whole point. A machine nobody has linked yet still has somebody
    /// sitting at it, and a blank first cell turns the list into machine names nobody recognises.
    /// The rule is <see cref="OwnerNames.For"/>, which ContactService.OwnerName on the server asks too.
    /// </summary>
    public string OwnerName => OwnerNames.For(ContactName, ClientName, RequestedBy, UserName);

    /// <summary>The first column is only the name typed at a support request — the lists mark it (2026-10-18).</summary>
    public bool NamedByRequest => OwnerNames.IsRequested(ContactName, ClientName, RequestedBy);
}

public sealed class SessionInfo
{
    public string SessionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    /// <summary>Technician who accepted the call / started the session. They auto-open the viewer.</summary>
    public string StartedBy { get; set; } = "";
    public List<string> Technicians { get; set; } = new();
    public bool HostConnected { get; set; }
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Hosted by the unattended agent rather than by the customer's tray app. The console
    /// needs this to answer one question honestly: after a reboot, will a host come back on its
    /// own, or does somebody have to be at the machine to accept?</summary>
    public bool Unattended { get; set; }
    /// <summary>Every viewer socket has gone without anybody saying "end", and the server is
    /// holding the session open in case one comes back. See TenantHub.BeginGrace.</summary>
    public bool Held { get; set; }
    /// <summary>
    /// The customer is sharing their screen from a web browser (3.8.2, 2026-09-30). The technician
    /// can SEE and chat; no browser lets anybody move the pointer or type on the machine, so the
    /// console sends no input and offers nothing that needs the support app.
    /// </summary>
    public bool ViewOnly { get; set; }
    /// <summary>
    /// The consent prompt is on the customer's screen and nobody has answered it yet (2026-10-02).
    /// The console shows the technician that it is waiting rather than nothing at all, and opens
    /// the session window only once they allow it.
    /// </summary>
    public bool AwaitingConsent { get; set; }
    /// <summary>How long the prompt waits before declining by itself, in seconds. 0 = not known.</summary>
    public int ConsentSeconds { get; set; }
    /// <summary>
    /// The session is over, and <see cref="EndReason"/> says why — "nobody answered within 60
    /// seconds", "declined". Before this a session that ended before anybody joined it looked
    /// exactly like one that had not started yet: no host, no technicians.
    /// </summary>
    public bool Ended { get; set; }
    public string EndReason { get; set; } = "";

    /// <summary>Somebody from ANOTHER company connected with a code (roadmap 3.9): who, as the machine's own
    /// company sees it — "Jane Smith (Smith & Co IT)". Empty for every ordinary session.</summary>
    public string Visitor { get; set; } = "";
    /// <summary>Set only in the copy sent to the VISITOR's console: this is somebody else's machine, so the
    /// session window offers screen and control and nothing else. The server enforces it either way.</summary>
    public bool Visiting { get; set; }
    /// <summary>The person at the machine ticked "let them copy files" when they allowed the visitor (3.9) — or,
    /// for somebody a technician brought in (§155), the technician ticked it when they let them in.</summary>
    public bool VisitorFiles { get; set; }
    /// <summary>The visitor was brought in by this company's technician, named here, with a code made in the
    /// session (2026-10-22, §155) — not by the person at the machine. Empty otherwise.</summary>
    public string InvitedBy { get; set; } = "";
    /// <summary>Set only in the copy sent to a visitor who is waiting: the TECHNICIAN who must let them in, not the
    /// person at the machine (§155). Their waiting window names them.</summary>
    public string AwaitingTechnician { get; set; } = "";
    /// <summary>
    /// The host is the customer's tray app, running as the person at the machine, rather than the
    /// background service's SYSTEM host. Such a session cannot see a UAC prompt or the lock
    /// screen, cannot drive an elevated window, and its scripts are not elevated — said once, in
    /// the session window, so a black picture at a UAC prompt is not a mystery.
    /// </summary>
    public bool HostedByApp { get; set; }
}

/// <summary>
/// Server -> the customer's tray app: what is actually happening on this machine right now.
///
/// It exists for two jobs that are really the same job. A device connection that appears in the
/// middle of a session used to be told nothing at all, so a tray app whose socket blinked stayed
/// on "support in progress" for ever (BUGS #2). And while the server is holding a session open
/// for a technician whose socket dropped, the person at the machine is entitled to know that
/// nobody is watching at this moment — the same reasoning that put the listening indicator there.
/// </summary>
/// <summary>
/// The person at the machine turning unattended access on or off, from the tray app.
///
/// It is a request rather than a statement: the SERVER decides and stores it, and answers with
/// <see cref="UnattendedStateMsg"/>. That way there is one copy of the fact. A flag kept on the
/// machine would have to be trusted by the thing enforcing it, and the thing enforcing it is the
/// hub — which refuses to start an unattended session to a device whose flag is off, whatever
/// that machine's own agent believes.
/// </summary>
public sealed class SetUnattendedMsg : Msg
{
    public bool On { get; set; }
}

/// <summary>
/// The person at the machine setting, changing or removing its unattended password, from the tray
/// app (2026-10-02). Clint: handy for somebody connecting to their own personal machine from the
/// office — with unattended access on and no password, every technician at the company can get in.
///
/// Empty removes it. Like <see cref="SetUnattendedMsg"/> it is a request: the server checks it
/// (<see cref="UnattendedPasswords.Problem"/>), keeps only a one-way hash, and answers with
/// <see cref="UnattendedStateMsg"/>. Only the CLIENT connection may send it — the agent is what
/// the password guards, and a technician must not be able to set or clear a machine's password
/// from the office. A technician working on the machine in a session can, through the tray app,
/// as the person there could.
/// </summary>
public sealed class SetUnattendedPasswordMsg : Msg
{
    public string Password { get; set; } = "";
    /// <summary>Echoed in the <see cref="UnattendedStateMsg"/> that answers it (FreeSetup). Optional.</summary>
    public string RequestId { get; set; } = "";
}

/// <summary>The ONE rule for an unattended password, asked by the tray app before it sends and by
/// the server before it stores.</summary>
public static class UnattendedPasswords
{
    public const int MinLength = 8;

    /// <summary>What is wrong with it, or empty. Empty input is not asked here: it means "remove".</summary>
    public static string Problem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"The password must be at least {MinLength} characters.";
        if (password.Length > 200) return "That password is too long.";
        return "";
    }
}

/// <summary>
/// Server -> console: the machines whose unattended password THIS technician has typed and the
/// server remembers (2026-10-06). Clint: "entering a password remembers it, but only for the
/// particular technician that entered it." Sent at sign-in and whenever it changes — a password
/// changed or removed at the machine forgets everyone's. The console leaves the password box out
/// for these; the server still decides, and a stale list only means one extra question.
/// Never carries the password: the server keeps none, only that it was typed right.
/// </summary>
public sealed class SavedPasswordsMsg : Msg
{
    public List<string> DeviceIds { get; set; } = new();
    /// <summary>Whether this person may see and clear OTHER people's (a supervisor or the owner), so the
    /// console offers "Saved passwords…" only to them. The server checks again.</summary>
    public bool MayManage { get; set; }
}

/// <summary>
/// Console -> server: forget a remembered unattended password. <see cref="TechnicianId"/> 0 is the
/// sender's own; anybody else's, or <see cref="All"/>, needs a supervisor or the owner.
/// </summary>
public sealed class ForgetSavedPasswordMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public int TechnicianId { get; set; }
    public bool All { get; set; }
}

/// <summary>Console -> server: who has this machine's password remembered (supervisor or owner only).</summary>
public sealed class SavedPasswordWhoRequestMsg : Msg { public string DeviceId { get; set; } = ""; }

/// <summary>Server -> console: the people who have it remembered — names, never the password.</summary>
public sealed class SavedPasswordWhoMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public List<SavedPasswordHolder> People { get; set; } = new();
}

public sealed class SavedPasswordHolder
{
    public int TechnicianId { get; set; }
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
}

/// <summary>
/// The person at the machine chose Exit in the tray app (2026-09-29). Clint: Exit must close the
/// program completely and show the machine as offline, and technicians must not be able to connect
/// — the agent service had gone on hosting unattended sessions as if nothing had happened.
///
/// The SERVER acts on it, for the reason <see cref="SetUnattendedMsg"/> gives: it is the one place
/// every connection passes through. The machine then reads as offline everywhere and every session
/// to it is refused, the agent's included, until the tray app connects again — the next logon, or
/// the shortcut. The agent stays connected behind that, so the monthly health check still runs.
/// Only a CLIENT connection may send it; a closed laptop lid or a shutdown never does.
/// </summary>
public sealed class ClientClosingMsg : Msg { }

// ---- outside help (roadmap 3.9, step 1, 2026-10-06) ----
// Clint: a customer of ours needs help from somebody else — an accounting firm, or another company on
// the platform — and "the client generate[s] a code" that the outsider connects with. The machine's
// own company switches it on (off by default, changeable per machine); the CUSTOMER makes the code.

/// <summary>
/// Whether outside help is allowed on a machine, and what a code is. The ONE place, asked by the hub,
/// the portal, the console's menu, the tray app and the harnesses.
/// </summary>
public static class OutsiderAccess
{
    /// <summary>Per machine: follow the company's switch, on for this machine, off for this machine.</summary>
    public const int AsCompany = 0, On = 1, Off = 2;

    public static int Normalise(int mode) => mode is On or Off ? mode : AsCompany;

    /// <summary>
    /// The company's switch is the default and a machine's own choice overrides it either way (Clint,
    /// 2026-10-06: "off being default, but can be toggled per machine") — so a company that keeps it
    /// off can still turn it on for the one machine that needs it.
    /// </summary>
    public static bool Allowed(bool companyOn, int mode) => Normalise(mode) switch
    {
        On => true,
        Off => false,
        _ => companyOn
    };

    /// <summary>Nine digits, ten minutes, used once.</summary>
    public const int Digits = 9;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxForLength = 80;

    /// <summary>"123 456 789" — read out over the phone in threes.</summary>
    public static string Format(string code) =>
        code.Length == Digits ? $"{code[..3]} {code[3..6]} {code[6..]}" : code;

    /// <summary>What was typed, as digits only: spaces and dashes are how people copy it.</summary>
    public static string Clean(string? typed) => new((typed ?? "").Where(char.IsDigit).ToArray());

    /// <summary>What is wrong with "who is it for", or empty. It is shown to the customer when the
    /// outsider connects, and to the machine's company, so it must say something.</summary>
    public static string ForProblem(string? who)
    {
        var w = (who ?? "").Trim();
        if (w.Length == 0) return "Say who the code is for — the person or company you are giving it to.";
        if (w.Length > MaxForLength) return $"Keep it under {MaxForLength} characters.";
        return "";
    }

    /// <summary>The console's menu line: where this machine stands.</summary>
    public static string Describe(bool companyOn, int mode) => Normalise(mode) switch
    {
        On => "Allowed on this machine",
        Off => "Not allowed on this machine",
        _ => companyOn ? "Allowed — your company has it on" : "Not allowed — your company has it off"
    };
}

/// <summary>
/// What the server holds for this machine about outside help, sent when the tray app connects and
/// whenever it changes. <see cref="Allowed"/> shows or hides the tray's item. A live code is described
/// (who for, until when) but never sent again: the server keeps only its hash, so the digits exist
/// only in the reply that made them.
/// </summary>
public sealed class OutsiderStateMsg : Msg
{
    public bool Allowed { get; set; }
    /// <summary>Who the live code is for; empty when there is none.</summary>
    public string LiveFor { get; set; } = "";
    public DateTime? LiveUntilUtc { get; set; }
}

/// <summary>The person at the machine asking for a code. Only the CLIENT connection may — the
/// server refuses the agent and anything from the office (§31's rule).</summary>
public sealed class OutsiderCodeRequestMsg : Msg { public string For { get; set; } = ""; }

/// <summary>The answer: the code, or why not. Any code made before it on this machine has stopped working.</summary>
public sealed class OutsiderCodeMsg : Msg
{
    public string Code { get; set; } = "";
    public string For { get; set; } = "";
    public DateTime? ExpiresUtc { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>Stop this machine's live code now. The CLIENT connection only.</summary>
public sealed class OutsiderCodeCancelMsg : Msg { }

/// <summary>A supervisor or the owner setting one machine's outside help, from the console.</summary>
public sealed class DeviceOutsiderModeMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public int Mode { get; set; }
}

/// <summary>
/// What the server holds for this machine, sent when the tray app connects and again whenever it
/// changes. The tray checkbox shows THIS rather than what it last clicked, so a machine whose
/// setting was changed while it was switched off comes back telling the truth.
/// </summary>
public sealed class UnattendedStateMsg : Msg
{
    /// <summary>Unattended access is allowed on this machine.</summary>
    public bool On { get; set; }
    /// <summary>
    /// Whether the agent service is actually connected. On is a permission; this is a capability,
    /// and they are different failures: "you have not turned it on" and "there is nothing here to
    /// turn on" want different words in front of the customer.
    /// </summary>
    public bool AgentOnline { get; set; }
    /// <summary>An unattended password is set on this machine.</summary>
    public bool HasPassword { get; set; }
    /// <summary>Why the server refused the last password sent, or empty. The tray app checks the
    /// same rule first, so this is only seen if the two ever disagree.</summary>
    public string PasswordProblem { get; set; } = "";
    /// <summary>The <see cref="SetUnattendedPasswordMsg.RequestId"/> this answers, or empty for a state sent for
    /// any other reason (2026-10-06). A machine is told its state twice as it connects, so without this an
    /// installer setting a password could take an old "has a password" for the answer to its new one.</summary>
    public string RequestId { get; set; } = "";
    /// <summary>This machine was reinstalled and the IT company has not yet confirmed it is the same machine
    /// (2026-10-07): the tick is kept, but unattended access waits for them. Older tray apps ignore it.</summary>
    public bool AwaitingConfirm { get; set; }
}

public sealed class SessionStateMsg : Msg
{
    public string SessionId { get; set; } = "";
    /// <summary>There is a live session on this machine.</summary>
    public bool Active { get; set; }
    /// <summary>Alive, but every technician's socket is gone and we are waiting for one to return.</summary>
    public bool Held { get; set; }
    public string TechnicianName { get; set; } = "";
    /// <summary>Whether a technician is listening to this machine's sound, so a tray app that
    /// reconnects mid-session does not silently lose the disclosure.</summary>
    public bool Listening { get; set; }
}

public sealed class StateMsg : Msg
{
    public string You { get; set; } = "";
    public List<string> OnlineTechnicians { get; set; } = new();
    /// <summary>Every technician in the company, online or not, so tickets can be reassigned to anyone.</summary>
    public List<string> AllTechnicians { get; set; } = new();
    public List<DeviceInfo> Devices { get; set; } = new();
    public List<CallInfo> Calls { get; set; } = new();
    public List<SessionInfo> Sessions { get; set; } = new();
    public List<ChatMsg> RecentChat { get; set; } = new();
    /// <summary>The company is on the free version (2026-10-06): the console draws only what
    /// <see cref="FreePlan"/> allows. The hub enforces it whatever the console draws.</summary>
    public bool Free { get; set; }
    /// <summary>The Basic version's computers paid for (2026-10-25: three in the price, more bought, at most 20) — 0 from an older server, read as three.</summary>
    public int MaxMachines { get; set; }
    /// <summary>The one-person plan is the Basic version (2026-10-25, §163): its computers may be connected to attended, the
    /// person there asked — so the console lists them all and needs no password for one without. False on the Free version.</summary>
    public bool Basic { get; set; }
    /// <summary>A supervisor or the owner on the full version: may remove a machine, with a reason (2026-10-06).</summary>
    public bool MayRemoveDevices { get; set; }
    /// <summary>A supervisor or the owner (the free version's one person is its owner): may confirm a reinstalled
    /// machine is the same machine, which lets unattended access work on it again (2026-10-07).</summary>
    public bool MayConfirmReinstall { get; set; }
    /// <summary>
    /// The console release this company may take now (auto-update, 2026-10-16), or null when there is none for it — not
    /// stamped, paused, or its ring not reached yet. A console older than it updates itself at its next start, and says
    /// so in its header until then. It is checked against <see cref="UpdateTrust"/> before anything runs.
    /// </summary>
    public UpdateRelease? ConsoleUpdate { get; set; }
    /// <summary>
    /// Job types (2026-10-27, HANDOVER §168): the ticket categories this company has switched OFF, by their stored value. New
    /// ticket and Edit leave them out; the server refuses them anyway. Empty from an older server — every category.
    /// </summary>
    public List<int> CategoriesOff { get; set; } = new();
    /// <summary>
    /// Photos a workshop machine needs when it is booked in, as the company has it NOW: 3, or 0 when it does not ask for them
    /// (Job types). Three when an older server says nothing — what it would refuse without. A ticket already open says its
    /// own in <see cref="TicketDetail.PhotosRequired"/>.
    /// </summary>
    public int BookingPhotos { get; set; } = 3;
}
public sealed class RingMsg : Msg { public CallInfo Call { get; set; } = new(); }
public sealed class CallUpdateMsg : Msg { public CallInfo Call { get; set; } = new(); }
public sealed class DeviceUpdateMsg : Msg { public DeviceInfo Device { get; set; } = new(); }
/// <summary>Console -> server: this reinstalled machine IS the one on the list (2026-10-07). A supervisor's or the
/// owner's; the hub decides (<c>TenantHub.ConfirmReinstallAsync</c>).</summary>
public sealed class ConfirmReinstallMsg : Msg { public string DeviceId { get; set; } = ""; }
/// <summary>Console -> server: take this machine off the list (the free version, 2026-10-06).</summary>
public sealed class DeviceRemoveMsg : Msg
{
    public string DeviceId { get; set; } = "";
    /// <summary>One of <see cref="RemovalReasons.All"/> — required on the full version (2026-10-06), unused on the free.</summary>
    public string Reason { get; set; } = "";
    public string Note { get; set; } = "";
}
/// <summary>
/// Tray app -> server: the person at the machine chose Uninstall… and Windows let the uninstaller run (2026-10-06,
/// Clint: "an uninstall option of the client app tray for someone who no longer wants to use the product"). The
/// machine leaves the company's list as "Uninstalled at the machine", so the company can see it went and why.
/// Only a CLIENT connection may send it — it speaks for the person at the machine (§31, §57).
/// </summary>
public sealed class ClientUninstallingMsg : Msg { }
/// <summary>Server -> consoles: a machine was taken off the list; drop its row.</summary>
public sealed class DeviceGoneMsg : Msg { public string DeviceId { get; set; } = ""; public string MachineName { get; set; } = ""; }
public sealed class SessionReadyMsg : Msg { public SessionInfo Session { get; set; } = new(); }
public sealed class TechPresenceMsg : Msg { public List<string> OnlineTechnicians { get; set; } = new(); }

/// <summary>Server tells a device (client or agent) to start hosting a session.</summary>
public sealed class StartSessionMsg : Msg
{
    public string SessionId { get; set; } = "";
    public string HostToken { get; set; } = "";
    public string TechnicianName { get; set; } = "";
    /// <summary>
    /// To the AGENT only: the person at the machine has not granted unattended access, so its host
    /// must ask them first, exactly as the tray app's would (2026-10-02). The agent hosts an
    /// attended session at all because only its SYSTEM host can see a UAC prompt, drive an
    /// elevated window or run a script as an administrator. Sent only to an agent that says
    /// <see cref="DeviceCan.AskConsent"/>.
    /// </summary>
    public bool AskConsent { get; set; }
    /// <summary>Somebody from ANOTHER company, let in with a code (3.9): their company's name. The host's
    /// prompt then says plainly that they are not this IT company, and what they can and cannot do.</summary>
    public string VisitorCompany { get; set; } = "";
    /// <summary>
    /// Show "Being controlled remotely" on the machine for the whole session (2026-10-06): the free version,
    /// whose computers have no tray app to say somebody is connected. The agent passes it to the host.
    /// </summary>
    public bool ShowNotice { get; set; }
}
public sealed class SessionEndedMsg : Msg { public string SessionId { get; set; } = ""; public string Reason { get; set; } = ""; }
/// <summary>Host sends this on the session socket when the person at the machine declines the session.</summary>
public sealed class DeclineSessionMsg : Msg { public string SessionId { get; set; } = ""; public string Reason { get; set; } = ""; }
/// <summary>
/// Host -> server, on the session socket: the person at the machine clicked Allow. A host that is
/// asking connects FIRST (with <c>consent=asking</c> on its address) so that the technician can be
/// shown that the question is on their screen — and sends nothing else until this.
/// </summary>
public sealed class ConsentGrantedMsg : Msg
{
    public string SessionId { get; set; } = "";
    /// <summary>Somebody from OUTSIDE the company (3.9): the person also ticked "let them copy files". Only the
    /// prompt for an outsider offers it; an older host never sends it, and absent is no.</summary>
    public bool Files { get; set; }
}

// ---- in-session ----
public sealed class MonitorInfo { public int Index { get; set; } public int X { get; set; } public int Y { get; set; } public int Width { get; set; } public int Height { get; set; } public string Name { get; set; } = ""; public bool Primary { get; set; } }
public sealed class MonitorsMsg : Msg { public List<MonitorInfo> Monitors { get; set; } = new(); }
public sealed class SubscribeMsg : Msg
{
    public int Monitor { get; set; }
    public bool On { get; set; }

    /// <summary>
    /// What the viewer can decode, best first — see <see cref="VideoCodec"/>. The host picks the
    /// first one it can actually produce and falls back to JPEG tiles when it can produce none.
    ///
    /// It is a LIST on the wire rather than a flag because both ends are ours and both are updated
    /// together today, and 4.6 auto-update is what ends that: from then on a console and a host can
    /// be different builds, and the one that knows fewer codecs has to be the one that decides.
    /// An older host ignores this field entirely and keeps sending tiles, which is why the default
    /// of "absent means tiles" is the right way round.
    /// </summary>
    public List<string> Codecs { get; set; } = new();
}

/// <summary>The names that may appear in <see cref="SubscribeMsg.Codecs"/>.</summary>
public static class VideoCodec
{
    /// <summary>The original: 128 px tiles, JPEG each, no knowledge of the previous frame.</summary>
    public const string JpegTiles = "jpeg-tiles";
    /// <summary>Hardware H.264 through Media Foundation. Stage 2.2.</summary>
    public const string H264 = "h264";
}
public sealed class QualityMsg : Msg { public int Jpeg { get; set; } = 60; public int Fps { get; set; } = 12; }

public sealed class MouseMsg : Msg
{
    public int Monitor { get; set; }
    public int X { get; set; }      // pixel within monitor
    public int Y { get; set; }
    public string Action { get; set; } = "move"; // move|down|up|wheel
    public int Button { get; set; }  // 0 left 1 right 2 middle
    public int Delta { get; set; }   // wheel
}
public sealed class KeyMsg : Msg { public int Vk { get; set; } public bool Down { get; set; } public bool Extended { get; set; } }
/// <summary>
/// Where the customer's pointer is, whether it is being shown at all, and which shape it is
/// wearing. Stage 2.4.
///
/// <see cref="ShapeId"/> is the cursor's Windows handle, which is stable for the life of a shape,
/// so the bitmap travels once (as a <see cref="BinKind.Cursor"/> frame) and every position update
/// after that is this small message. A console that does not recognise the id yet draws its old
/// ring until the bitmap turns up, so a dropped shape frame costs a moment of ugliness rather than
/// a missing pointer.
///
/// <see cref="Visible"/> matters more than it looks: a full-screen video or a game hides the
/// pointer, and a console that kept drawing one would be showing the technician something that is
/// not on the customer's screen.
/// </summary>
public sealed class CursorMsg : Msg
{
    public int Monitor { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool Visible { get; set; } = true;
    public long ShapeId { get; set; }
}
public sealed class ParticipantsMsg : Msg { public List<string> Technicians { get; set; } = new(); public bool HostConnected { get; set; } }
// Clipboard text, in whichever direction it is travelling. Direction is implicit and needs no
// flag: a viewer only ever receives this from the host ("here is the customer's clipboard") and
// the host only ever receives it from a viewer ("put this on the customer's clipboard").
// Error is set when the host could not do it, so a failure is reported rather than looking like
// an empty clipboard — that distinction is the whole difficulty with clipboard code.
public sealed class ClipboardMsg : Msg
{
    public string Text { get; set; } = "";
    public bool Truncated { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// A file list on the clipboard (CF_HDROP), as absolute paths ON THE MACHINE THAT SENT THIS.
    ///
    /// The paths are useless to the far end on their own — "D:\Jobs\report.docx" means nothing on
    /// the other machine — so they are not a transfer. They are what was copied, so the other end
    /// can ask for it with a FileSetPullMsg, or say what is on offer. The real bytes always move
    /// as a file set.
    /// </summary>
    public List<string>? Files { get; set; }

    /// <summary>
    /// What <see cref="Files"/> would cost to fetch: total bytes of the whole selection, folders
    /// walked. Zero when it is not a file list, or when it could not be measured.
    ///
    /// This is the number the size rule is made of (stage 3.5.1). A clipboard file list is paths,
    /// not bytes, so the receiving end has to fetch before a paste can work — and fetching four
    /// gigabytes because somebody pressed Ctrl+C over a video folder would stall the session for
    /// something nobody asked for. Under the threshold it is fetched silently; over it, the far
    /// end is told what is on offer and fetches nothing until it is asked to.
    /// </summary>
    public long Bytes { get; set; }

    /// <summary>
    /// True when a clipboard watcher pushed this because the clipboard changed; false when it is
    /// the answer to a <see cref="ClipboardRequestMsg"/>.
    ///
    /// The difference matters at the far end. An answer to a request was asked for and should say
    /// so plainly; a push is meant to be invisible when it works and only worth a word when it
    /// does not. An older console never sets it, which is the right default for one.
    /// </summary>
    public bool Auto { get; set; }
}

/// <summary>A technician asking for the customer's clipboard. Answered with a ClipboardMsg.</summary>
public sealed class ClipboardRequestMsg : Msg { }

public sealed class FileOfferMsg : Msg { public string FileId { get; set; } = ""; public string Name { get; set; } = ""; public long Size { get; set; } public string? TargetDir { get; set; } }
public sealed class FileAcceptMsg : Msg { public string FileId { get; set; } = ""; public bool Accept { get; set; } = true; }
public sealed class FileDoneMsg : Msg { public string FileId { get; set; } = ""; public bool Ok { get; set; } public string? Error { get; set; } public string? SavedPath { get; set; } }
public sealed class FileListRequestMsg : Msg { public string Path { get; set; } = ""; }
public sealed class FileEntry { public string Name { get; set; } = ""; public bool IsDir { get; set; } public long Size { get; set; } public DateTime Modified { get; set; } }
public sealed class FileListMsg : Msg { public string Path { get; set; } = ""; public List<FileEntry> Entries { get; set; } = new(); public string? Error { get; set; } }
/// <summary>Technician asks host to send a file from the client machine.</summary>
public sealed class FilePullMsg : Msg { public string Path { get; set; } = ""; public string FileId { get; set; } = ""; }

// ---- whole trees: several files, folders, and folders inside folders -------------------------
//
// The single-file messages above move one file with a name and a size. That is not enough for
// copy and paste or for drag and drop, where someone selects three folders and expects all of it
// to arrive with its shape intact — including the empty folders, which carry meaning.
//
// A set is a flat list of entries, each with a path RELATIVE to the root of the transfer. The
// receiver rebuilds the tree from those. Chunks reuse BinKind.FileChunk with a file id of
// "<setId>#<entry index>", so the binary framing and its parser did not have to change at all.

/// <summary>
/// Well-known values for <see cref="FileSetOfferMsg.TargetDir"/>. The sending side cannot know
/// the receiving machine's profile path — and must not guess, because on the customer's machine
/// the host runs as SYSTEM and SYSTEM's own desktop is not the one anybody is looking at. So it
/// asks by name and the receiver resolves it.
/// </summary>
/// <summary>
/// Folders on the CUSTOMER's machine that the technician's end can ask for by name but must never
/// guess. The host runs as SYSTEM, so its own profile is
/// C:\Windows\System32\config\systemprofile and the logged-on user's is something only that
/// machine knows. A sentinel travels, and <c>HostPaths.Resolve</c> on the host turns it into a
/// real path the moment it arrives — in ONE place, for listing, pulling and receiving alike.
///
/// It was half wired once: resolved for a dropped file set and nowhere else, so listing %DESKTOP%
/// was treated as a RELATIVE path against the host's working directory and produced
/// "Could not find a part of the path 'C:\Program Files\Anywhere IT\Remote Support\%DESKTOP%'".
/// See docs/BUGS.md #6.
/// </summary>
public static class RemotePaths
{
    public const string Desktop = "%DESKTOP%";
    public const string Downloads = "%DOWNLOADS%";
    public const string Documents = "%DOCUMENTS%";

    /// <summary>
    /// Where files go when they are on their way to the customer's CLIPBOARD rather than to a
    /// folder — a staging area under their temp, not a destination anybody chose.
    ///
    /// A clipboard file list is a list of PATHS, so before the customer can paste anything the
    /// bytes have to already exist somewhere on their disk. That somewhere used to be their
    /// Downloads, which is wrong in a way that only shows up when it works: the technician copies
    /// three files, pastes onto the customer's desktop, gets three files on the desktop — and
    /// three more sitting in Downloads that nobody asked for and nobody will delete. Reported
    /// exactly that way on 2026-09-18.
    ///
    /// The other direction solved this on 2026-09-15 and is worth copying rather than rethinking:
    /// <c>DragOut.StagingRoot</c> puts files under %TEMP% for precisely this reason — "these are a
    /// byproduct of a gesture, and Explorer copies them out on the drop". Paste is the same
    /// gesture with the same byproduct, pointing the other way.
    /// </summary>
    public const string PasteStaging = "%PASTE%";

    /// <summary>Where a technician's file waits to be printed on the customer's printer (remote printing,
    /// 2026-10-06): under their temp, and the ONLY place the host will print from.</summary>
    public const string PrintStaging = "%PRINT%";

    /// <summary>True when this is a sentinel rather than a real path, so a caller can say which.</summary>
    public static bool IsSentinel(string? path) =>
        path != null && path.Length > 2 && path[0] == '%' && path[^1] == '%';
}

/// <summary>One item in a set: a file to copy, or a folder to create (including an empty one).</summary>
public sealed class FileSetEntry
{
    /// <summary>Relative to the root of the transfer, always with '/' separators. Never absolute
    /// and never containing "..": the receiver rejects the whole set otherwise, because the
    /// receiving side may be running as SYSTEM and a crafted path would write anywhere on disk.</summary>
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public long Size { get; set; }
}

public sealed class FileSetOfferMsg : Msg
{
    public string SetId { get; set; } = "";
    public List<FileSetEntry> Entries { get; set; } = new();
    public long TotalBytes { get; set; }
    /// <summary>Where to put it. Null means the receiver chooses — the logged-on user's Downloads.</summary>
    public string? TargetDir { get; set; }
    /// <summary>What was selected, so the far end can name it in the UI.</summary>
    public string Label { get; set; } = "";
}

public sealed class FileSetAcceptMsg : Msg
{
    public string SetId { get; set; } = "";
    public bool Accept { get; set; } = true;
    public string? Error { get; set; }
    /// <summary>The folder the receiver actually created, once it knows.</summary>
    public string? TargetDir { get; set; }
}

public sealed class FileSetProgressMsg : Msg
{
    public string SetId { get; set; } = "";
    public long BytesDone { get; set; }
    public long TotalBytes { get; set; }
    public int FilesDone { get; set; }
    public int FilesTotal { get; set; }
    public string Current { get; set; } = "";
}

public sealed class FileSetDoneMsg : Msg
{
    public string SetId { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>The folder everything landed in.</summary>
    public string? SavedDir { get; set; }
    /// <summary>The top-level items inside it, as absolute paths on the receiving machine. This is
    /// what goes onto a clipboard as a file list, so a paste lands the right things.</summary>
    public List<string> SavedPaths { get; set; } = new();
    /// <summary>Whatever could not be read or written, with the reason. A transfer that silently
    /// skipped three locked files is not a success and must not be reported as one.</summary>
    public List<string> Skipped { get; set; } = new();
}

/// <summary>
/// A technician has started dragging on the picture of the customer's screen (roadmap 3.5.2): what
/// does the person there have selected in the folder window in front of them? Asked at the START of
/// the drag, so the answer is usually back before the pointer reaches the edge of the window.
/// </summary>
public sealed class SelectionRequestMsg : Msg
{
    public string RequestId { get; set; } = "";
}

/// <summary>
/// The answer: the selected files and folders in the Explorer window (or on the desktop) in front,
/// read by a copy of the host running as the person. Empty <see cref="Paths"/> always comes with
/// <see cref="Why"/>, in words a technician can act on — a drag that silently does nothing is the
/// failure this exists to avoid.
/// </summary>
public sealed class SelectionMsg : Msg
{
    public string RequestId { get; set; } = "";
    /// <summary>"explorer", "desktop", or empty when the window in front is not a folder.</summary>
    public string Where { get; set; } = "";
    public string Folder { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public string Why { get; set; } = "";
}

/// <summary>
/// The technician turning the privacy screen on or off (roadmap 1.7): the customer's monitors go
/// black with one line saying who is working, and the technician's picture is unaffected.
/// </summary>
public sealed class PrivacyScreenMsg : Msg
{
    public bool On { get; set; }
}

/// <summary>
/// What the customer's screen IS doing, from the host — never what was asked for. A privacy screen
/// the technician believes is on and is not is the failure this feature must not have, so the
/// console's menu follows this message, and <see cref="Error"/> says why when it would not come up.
/// The server keeps the last one to tell a technician who joins later, and writes it on the ticket.
/// </summary>
public sealed class PrivacyStateMsg : Msg
{
    public bool On { get; set; }
    public string Error { get; set; } = "";
    /// <summary>How many monitors are covered, so "on" says how much of their screen it means.</summary>
    public int Monitors { get; set; }
}

/// <summary>
/// A technician started or stopped recording the session (roadmap 1.6). Sent by the console on the
/// session socket; the server writes it on the ticket and passes it to the customer's tray app,
/// which says so in its log — the person at the machine is told they are being recorded, the same
/// way a file taken off their machine is disclosed (Clint, 2026-09-29).
/// </summary>
public sealed class RecordingStateMsg : Msg
{
    public bool On { get; set; }
    /// <summary>Who pressed Record. Filled in by the server, never trusted from the console.</summary>
    public string By { get; set; } = "";
}

/// <summary>A technician asking for these paths from the customer's machine, folders and all.</summary>
public sealed class FileSetPullMsg : Msg
{
    public string SetId { get; set; } = "";
    public List<string> Paths { get; set; } = new();
}

/// <summary>
/// Something moved on or off the customer's machine, reported BY THE HOST once it has actually
/// happened — not by the end that asked for it, which only knows what it requested. That
/// distinction is the lesson of 2026-09-15: three faults in a row were each reported as a success
/// by the layer underneath, every one of them truthfully answering the wrong question.
///
/// It exists for the person sitting at the machine. A technician can pull a file with nothing at
/// all shown on the customer's screen, and files are not obviously different in kind from sound,
/// which has been disclosed since §18. Clint's decision on 2026-09-15 was to log it rather than
/// announce it: it goes into the tray app's chat log and onto the ticket, with no banner and no
/// balloon, so it is auditable afterwards without interrupting the customer while they are being
/// helped.
/// </summary>
public sealed class FileActivityMsg : Msg
{
    /// <summary>True when it LEFT this machine (the technician took it), false when it arrived.</summary>
    public bool FromMachine { get; set; }
    /// <summary>What moved, named as it is named on the customer's machine.</summary>
    public string Label { get; set; } = "";
    public int FileCount { get; set; }
    public long Bytes { get; set; }
    /// <summary>Set when it did not work. A refusal is worth logging too.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// What kind of movement this was: <c>files</c> — the default, and a real transfer — or
    /// <c>clip-text</c>, text crossing between the two clipboards.
    ///
    /// Clipboard FILES deliberately have no kind of their own. Copying files only puts paths on a
    /// clipboard; the bytes move afterwards as an ordinary file set, and that transfer already
    /// reports itself through this same message. A second line at the moment of the copy would
    /// double-count every paste and leave the ticket disagreeing with itself.
    /// </summary>
    public string Kind { get; set; } = "files";

    /// <summary>
    /// How many separate movements this line stands for. Only <c>clip-text</c> ever sets it above
    /// one.
    ///
    /// Text is coalesced before it is reported. A technician fetching a licence key, trimming it
    /// and sending it back is three clipboard changes inside twenty seconds, and three ticket
    /// notes for that would bury the notes somebody actually wrote. One line per direction per
    /// quiet minute says the same thing and can still be read.
    /// </summary>
    public int Times { get; set; } = 1;

    /// <summary>For the print kinds (2026-10-06): the printer it went to. Empty for everything else.</summary>
    public string Printer { get; set; } = "";

    /// <summary>Set by the host when the files went straight to, or came straight from, ANOTHER machine the technician
    /// had open (2026-10-15, <see cref="BridgeKeyMsg"/>) rather than through the technician's PC.</summary>
    public string BridgeId { get; set; } = "";

    /// <summary>That other machine's name, filled in by the SERVER — never believed from a host — and only on what goes
    /// to the ticket and the technicians. The person at the machine is told "another computer": it may belong to a
    /// different customer (Clint's answer 3, 2026-10-15).</summary>
    public string OtherMachine { get; set; } = "";

    private static string Quote(string s) => "“" + (string.IsNullOrWhiteSpace(s) ? "a document" : s) + "”";

    /// <summary>
    /// One sentence for it, in ONE place. The ticket note, the tray app's chat log and the
    /// technician's own status bar all read from here, because a number or a name that differs
    /// between two of them makes the reader distrust all three — the lesson of the trial-notice
    /// day count in §20.
    /// </summary>
    public string Describe(string technician)
    {
        var who = string.IsNullOrWhiteSpace(technician) ? "The technician" : technician;

        // Remote printing (2026-10-06). Label is the document's name; Printer the printer it went to.
        if (Kind == "print-out")
            return Error != null
                ? $"Printing {Quote(Label)} to {who}'s printer did not work — {Error}."
                : $"{Quote(Label)} was printed on this computer to {who}'s printer{(Printer.Length > 0 ? $" ({Printer})" : "")}.";
        if (Kind == "print-in")
            return Error != null
                ? $"{who} tried to print {Quote(Label)} on this computer's printer — {Error}."
                : $"{who} printed {Quote(Label)} on this computer's printer{(Printer.Length > 0 ? $", {Printer}" : "")}" +
                  $"{(FileCount > 0 ? $" ({FileCount} page{(FileCount == 1 ? "" : "s")})" : "")}.";

        if (Kind == "clip-text")
        {
            // A count of characters, never the characters. This is the feature where the
            // clipboard is very often a password, and a ticket note outlives a trace file.
            var amount = Bytes > 0 ? $"{Bytes:N0} characters" : "text";
            var often = Times > 1 ? $", {Times} times" : "";
            if (Error != null) return $"Text could not be shared with {who} — {Error}.";
            return FromMachine
                ? $"Text copied on this computer was shared with {who} ({amount}{often})."
                : $"{who} put text on this computer's clipboard ({amount}{often}).";
        }

        var size = Bytes > 0 ? $", {FileSetLimits.Human(Bytes)}" : "";
        var what = FileCount > 1 ? $"{Label} ({FileCount} files{size})"
                 : Bytes > 0 ? $"{Label} ({FileSetLimits.Human(Bytes)})"
                 : Label;
        if (BridgeId.Length > 0)
        {
            var other = string.IsNullOrWhiteSpace(OtherMachine) ? "another computer" : OtherMachine;
            if (Error != null)
                return FromMachine
                    ? $"{who} tried to copy {what} from this computer straight to {other} — {Error}."
                    : $"{who} tried to send {what} to this computer straight from {other} — {Error}.";
            return FromMachine
                ? $"{who} copied {what} from this computer straight to {other}."
                : $"{who} sent {what} to this computer straight from {other}.";
        }
        if (Error != null)
            return FromMachine
                ? $"{who} tried to copy {what} from this computer — {Error}."
                : $"{who} tried to send {what} to this computer — {Error}.";
        return FromMachine
            ? $"{who} copied {what} from this computer."
            : $"{who} sent {what} to this computer.";
    }
}

public sealed class ScriptRunMsg : Msg { public string RunId { get; set; } = ""; public string Language { get; set; } = "powershell"; public string Script { get; set; } = ""; }
public sealed class ScriptOutputMsg : Msg { public string RunId { get; set; } = ""; public string Text { get; set; } = ""; public bool IsError { get; set; } public bool Done { get; set; } public int? ExitCode { get; set; } }
public sealed class ScriptKillMsg : Msg { public string RunId { get; set; } = ""; }

public sealed class SysInfoMsg : Msg { public Dictionary<string, string> Info { get; set; } = new(); }
public sealed class SysActionMsg : Msg { public string Action { get; set; } = ""; } // reboot|lock|ctrl_alt_del

/// <summary>
/// Host -> technician: what actually happened when a SysActionMsg was carried out. A reboot that
/// Windows refused looks exactly like a reboot that is about to happen, and the session window
/// would otherwise sit waiting for a machine that was never going anywhere.
/// </summary>
public sealed class SysActionResultMsg : Msg
{
    public string Action { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Hearing the customer's machine. The technician asks, the host captures what the speakers are
// being fed (WASAPI loopback) and streams it as binary audio frames.
// ---------------------------------------------------------------------------

/// <summary>Technician -> host: start or stop sending me the sound of that machine.</summary>
public sealed class AudioListenMsg : Msg { public bool On { get; set; } }

/// <summary>Technician -> host: tell me everything about that machine's sound hardware.</summary>
public sealed class AudioDevicesRequestMsg : Msg { }

/// <summary>
/// One playback or recording endpoint as Windows sees it.
///
/// The state matters as much as the name. A device the customer insists they do not have is
/// usually present and DISABLED, and a device they swear is connected is often UNPLUGGED —
/// and those are different faults with different fixes.
/// </summary>
public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The physical adaptor behind it — what tells "Speakers" from "Speakers".</summary>
    public string Adaptor { get; set; } = "";
    /// <summary>ACTIVE, DISABLED, UNPLUGGED or NOTPRESENT.</summary>
    public string State { get; set; } = "";
    public bool IsCapture { get; set; }
    /// <summary>Windows has three separate defaults and they can disagree. A machine whose
    /// multimedia default is an unplugged television plays music at nobody while system sounds
    /// come out of the speakers perfectly — which is exactly what "sound stopped working"
    /// sounds like when described over the telephone.</summary>
    public bool IsDefaultConsole { get; set; }
    public bool IsDefaultMultimedia { get; set; }
    public bool IsDefaultCommunications { get; set; }
    public int VolumePercent { get; set; } = -1;
    public bool Muted { get; set; }
    /// <summary>Non-empty when the endpoint's volume control could not be read at all.</summary>
    public string Error { get; set; } = "";
}

/// <summary>One application holding the playback device open, and what the mixer is doing to it.</summary>
public sealed class AudioAppSession
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    /// <summary>playing, idle or expired.</summary>
    public string State { get; set; } = "";
    public int VolumePercent { get; set; } = -1;
    public bool Muted { get; set; }
    public bool IsSystemSounds { get; set; }
}

/// <summary>
/// Host -> technician: the whole picture of that machine's audio.
///
/// This is the half neither TeamViewer nor AnyDesk offers. Streaming tells a technician the
/// machine is silent; this tells them WHY — the default device is a television nobody is
/// watching, the endpoint is muted, one application is muted in the mixer, or the driver has a
/// problem code.
/// </summary>
public sealed class AudioDevicesMsg : Msg
{
    public List<AudioDeviceInfo> Devices { get; set; } = new();
    /// <summary>Applications on the default playback device.</summary>
    public List<AudioAppSession> Apps { get; set; } = new();
    /// <summary>Audiosrv and AudioEndpointBuilder, as "name: status". If the endpoint builder is
    /// stopped there are no endpoints to find and every other answer is a consequence.</summary>
    public List<string> Services { get; set; } = new();
    public string Error { get; set; } = "";
}

/// <summary>
/// Host -> technician: what the audio side is doing, and why it might be silent. Sent when
/// listening starts or stops, when it fails, and whenever the endpoint changes under us.
///
/// <see cref="Muted"/> and <see cref="VolumePercent"/> are carried because they are the first
/// thing a technician will blame for silence. Measured 2026-09-13 on two machines: loopback
/// capture is taken BEFORE the endpoint's volume and mute are applied, so neither one affects
/// what the technician hears. They are shown so nobody chases that ghost, not because they
/// explain silence here.
/// </summary>
public sealed class AudioStatusMsg : Msg
{
    public bool Listening { get; set; }
    public string Device { get; set; } = "";
    public bool Muted { get; set; }
    public int VolumePercent { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    /// <summary>Empty when all is well. A failure here is the whole diagnosis, so it is a
    /// sentence a technician can read, not an HRESULT on its own.</summary>
    public string Error { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Talking to the customer (3.3, 2026-09-30; docs/NEXT-SESSION-VOICE.md). Voice is not Listen:
// Listen is the machine's own sound, one way, with a buffer made for music; voice is two people,
// both ways, with a buffer made for conversation. They share nothing but the frame layout.
//
// Clint's three answers govern all of it. The technician's voice reaching the customer's speakers
// needs no permission. The customer's MICROPHONE opens only when THEY allow it, every session, and
// never in an unattended session — the hub enforces that, because the hub is the only party that
// knows for certain which kind of session it is. Recordings stay picture only.
// ---------------------------------------------------------------------------

/// <summary>
/// Technician -> host: my Talk is on (or off). The hub stamps <see cref="Technician"/> from the
/// socket it came in on, so a console cannot speak in somebody else's name. Muting is NOT this
/// message: a muted technician is still in the call (their end simply stops sending frames), and
/// the host must keep its playback open throughout — see <see cref="VoiceStateMsg"/>.
/// </summary>
public sealed class VoiceTalkMsg : Msg
{
    public bool On { get; set; }
    public string Technician { get; set; } = "";
}

/// <summary>
/// Host -> technicians AND the person at the machine (the hub carries it to their tray app, the
/// way it carries the listening disclosure): what the call is doing, in one message, sent on every
/// change.
/// </summary>
public sealed class VoiceStateMsg : Msg
{
    /// <summary>A technician's voice is coming out of this machine's speakers.</summary>
    public bool Call { get; set; }
    /// <summary>Who is talking, for the customer's banner. Several are joined by " and ".</summary>
    public string Technicians { get; set; } = "";
    /// <summary>One of <see cref="VoiceMic"/>: where the customer's microphone stands.</summary>
    public string Mic { get; set; } = VoiceMic.Off;
    /// <summary>A sentence a technician (or customer) can read: why the microphone is where it
    /// is, when that is not obvious. Empty otherwise.</summary>
    public string Detail { get; set; } = "";
}

/// <summary>Where the customer's microphone stands. Strings on the wire, because the browser page
/// speaks this protocol too and a number would mean nothing in its source.</summary>
public static class VoiceMic
{
    /// <summary>No call, or the call has not asked yet.</summary>
    public const string Off = "off";
    /// <summary>The customer has been asked and has not answered.</summary>
    public const string Asking = "asking";
    /// <summary>They allowed it and it is open: the technician can hear them.</summary>
    public const string On = "on";
    /// <summary>They said no, or turned it off again. They can change their mind from the banner.</summary>
    public const string Declined = "declined";
    /// <summary>They allowed it and it could not be opened — no microphone, Windows' privacy
    /// setting says no, or the device refused. <see cref="VoiceStateMsg.Detail"/> says which.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>An unattended session. Nobody is asked, and nothing an answer says is obeyed.</summary>
    public const string Unattended = "unattended";
}

/// <summary>
/// The person at the machine -> host, from their tray app (the hub carries it across): use my
/// microphone, or stop using it. Refused by the hub in an unattended session, and only ever
/// accepted from the CLIENT connection — the agent is SYSTEM and nobody clicked anything there.
/// </summary>
public sealed class VoiceMicAnswerMsg : Msg { public bool Allow { get; set; } }

/// <summary>What a voice frame carries. 16 kHz mono is what Windows' echo canceller produces and
/// what telephone-quality speech needs; 20 ms frames keep the latency a conversation can bear.</summary>
public static class VoiceWire
{
    public const int Rate = 16000;
    public const int FrameMs = 20;
    public const int FrameSamples = Rate * FrameMs / 1000;   // 320 samples, 640 bytes
}

// ---------------------------------------------------------------------------
// Binary frames. Byte 0 = kind. Little-endian ints.
// ---------------------------------------------------------------------------
public static class BinKind
{
    public const byte Tiles = 1;      // [1][monitor:i32][count:i32] then count * ([x:i32][y:i32][w:i32][h:i32][len:i32][jpeg])
    public const byte FileChunk = 2;  // [2][idLen:u8][id utf8][offset:i64][data]
    public const byte Audio = 3;      // [3][seq:u32][sampleRate:u32][channels:u8][format:u8][pcm]
    public const byte Video = 4;      // [4][monitor:i32][flags:u8][width:i32][height:i32][annex-B H.264]
    // The customer's real pointer bitmap, sent once per distinct shape and then referred to by id.
    // Stage 2.4. A duplicated frame never contains the cursor — Windows composes it separately --
    // which is why the console used to draw a ring of its own.
    public const byte Cursor = 5;     // [5][shapeId:i64][w:i32][h:i32][hotX:i32][hotY:i32][png]
    // A voice, either way (3.3): the technician's to the host, the customer's to the technicians.
    // The same layout as Audio, so one parser reads both — but its own kind, because the console
    // plays the two through different players and a Listen stream must never reach a voice buffer.
    public const byte Voice = 6;      // [6][seq:u32][sampleRate:u32][channels:u8][format:u8][pcm]
    // End-to-end encryption (2026-10-07, SessionSeal): a MESSAGE sealed, its JSON type name visible for the
    // allow-lists: [7][tagLen:u8][tag utf8][sender:u32][counter:u64][ciphertext + 16-byte GCM tag]
    public const byte Sealed = 7;
    // A sealed BINARY frame keeps its kind with this bit set: [kind|0x80][sender:u32][counter:u64][ciphertext + tag]
    public const byte SealedBit = 0x80;
    // Files going straight from one machine to another (2026-10-15, BridgeSeal): host to host through the hub, sealed under
    // a key of their own, the bridge id clear so the hub can route it: [8][idLen:u8][id ascii][sender:u32][counter:u64][ct + tag]
    public const byte Bridge = 8;

    /// <summary>The kind of a frame, sealed or not — what every drop rule and allow-list must ask, never byte 0 itself.</summary>
    public static byte Of(byte first) => (byte)(first & 0x7F);
}

/// <summary>Bits in the flags byte of a <see cref="BinKind.Video"/> frame.</summary>
public static class VideoFlags
{
    /// <summary>
    /// A viewer with no picture can start decoding from this frame.
    ///
    /// It is on the wire rather than left to the decoder to work out, because a decoder fed a
    /// predicted frame first does not fail — it builds a picture from a reference frame it never
    /// saw and produces a smear that slowly corrects itself, which is indistinguishable from a bad
    /// link. The receiver drops everything until this bit arrives.
    /// </summary>
    public const byte KeyFrame = 1;
}

/// <summary>Sample formats an audio frame can carry. Only one so far, deliberately.</summary>
public static class AudioFormat
{
    /// <summary>Signed 16-bit little-endian, interleaved. What the first version streams.</summary>
    public const byte Pcm16 = 1;
}

public static class Bin
{
    public static byte[] FileChunk(string fileId, long offset, ReadOnlySpan<byte> data)
    {
        var id = System.Text.Encoding.UTF8.GetBytes(fileId);
        var buf = new byte[1 + 1 + id.Length + 8 + data.Length];
        buf[0] = BinKind.FileChunk; buf[1] = (byte)id.Length;
        id.CopyTo(buf, 2);
        BitConverter.TryWriteBytes(buf.AsSpan(2 + id.Length), offset);
        data.CopyTo(buf.AsSpan(2 + id.Length + 8));
        return buf;
    }
    /// <summary>
    /// One packet of the customer's sound. The FORMAT TRAVELS WITH THE DATA on purpose: the
    /// user can change their playback device mid-session, and a separate "the format is now X"
    /// message could arrive out of order with the samples it describes. Eleven bytes of header
    /// on a packet of a few thousand is a cheap way to make that impossible.
    /// </summary>
    public const int AudioHeader = 1 + 4 + 4 + 1 + 1;

    public static byte[] Audio(uint seq, int sampleRate, int channels, byte format, ReadOnlySpan<byte> pcm)
    {
        var buf = new byte[AudioHeader + pcm.Length];
        buf[0] = BinKind.Audio;
        BitConverter.TryWriteBytes(buf.AsSpan(1), seq);
        BitConverter.TryWriteBytes(buf.AsSpan(5), (uint)sampleRate);
        buf[9] = (byte)channels;
        buf[10] = format;
        pcm.CopyTo(buf.AsSpan(AudioHeader));
        return buf;
    }

    /// <summary>One frame of a voice (<see cref="BinKind.Voice"/>): the audio layout under its own
    /// kind. Read back with <see cref="ParseAudio"/>.</summary>
    public static byte[] Voice(uint seq, ReadOnlySpan<byte> pcm)
    {
        var buf = Audio(seq, VoiceWire.Rate, 1, AudioFormat.Pcm16, pcm);
        buf[0] = BinKind.Voice;
        return buf;
    }

    public static (uint seq, int sampleRate, int channels, byte format, ReadOnlyMemory<byte> pcm) ParseAudio(ReadOnlyMemory<byte> frame)
    {
        var s = frame.Span;
        uint seq = BitConverter.ToUInt32(s.Slice(1, 4));
        int rate = (int)BitConverter.ToUInt32(s.Slice(5, 4));
        return (seq, rate, s[9], s[10], frame.Slice(AudioHeader));
    }

    /// <summary>
    /// One encoded video frame. The header is fourteen bytes on a packet of a few thousand, and
    /// every field in it is there because the alternative was an assumption.
    ///
    /// The SIZE travels with the frame because H.264 works in macroblocks and the encoder rounds
    /// an odd screen up; the viewer must know the coded size to draw the picture and the monitor
    /// size to crop it, and those are not always the same number.
    /// </summary>
    public const int VideoHeader = 1 + 4 + 1 + 4 + 4;

    public static byte[] Video(int monitor, bool keyFrame, int width, int height, ReadOnlySpan<byte> bitstream)
    {
        var buf = new byte[VideoHeader + bitstream.Length];
        buf[0] = BinKind.Video;
        BitConverter.TryWriteBytes(buf.AsSpan(1), monitor);
        buf[5] = keyFrame ? VideoFlags.KeyFrame : (byte)0;
        BitConverter.TryWriteBytes(buf.AsSpan(6), width);
        BitConverter.TryWriteBytes(buf.AsSpan(10), height);
        bitstream.CopyTo(buf.AsSpan(VideoHeader));
        return buf;
    }

    public static (int monitor, bool keyFrame, int width, int height, ReadOnlyMemory<byte> bitstream) ParseVideo(ReadOnlyMemory<byte> frame)
    {
        var s = frame.Span;
        int monitor = BitConverter.ToInt32(s.Slice(1, 4));
        bool key = (s[5] & VideoFlags.KeyFrame) != 0;
        int w = BitConverter.ToInt32(s.Slice(6, 4));
        int h = BitConverter.ToInt32(s.Slice(10, 4));
        return (monitor, key, w, h, frame.Slice(VideoHeader));
    }

    public static (string id, long offset, ReadOnlyMemory<byte> data) ParseFileChunk(ReadOnlyMemory<byte> frame)
    {
        var s = frame.Span; int idLen = s[1];
        var id = System.Text.Encoding.UTF8.GetString(s.Slice(2, idLen));
        long off = BitConverter.ToInt64(s.Slice(2 + idLen, 8));
        return (id, off, frame.Slice(2 + idLen + 8));
    }
}

// ---- website chat (stage 3.8.3, 2026-09-30): a visitor to the company's own website ----

/// <summary>One conversation with a website visitor, as every signed-in person sees it.</summary>
public sealed class WebChatInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>The page of the company's website they started on.</summary>
    public string Page { get; set; } = "";
    /// <summary>Who replied first and so owns it; empty while nobody has.</summary>
    public string AnsweredBy { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public bool Ended { get; set; }
    public string EndedBy { get; set; } = "";
    /// <summary>Their page is open right now. False once they have closed it or gone elsewhere.</summary>
    public bool VisitorHere { get; set; }
    public string LastText { get; set; } = "";
    /// <summary>Set only on the update a hand-over sends (2026-10-15): who gave it to <see cref="AnsweredBy"/>. Not
    /// stored. The person it went to is brought the chat and dinged; their own move (a first reply, or taking
    /// it themselves) is not news to them.</summary>
    public string HandedOverBy { get; set; } = "";
}

public sealed class WebChatLineInfo
{
    public long Id { get; set; }
    public int ChatId { get; set; }
    public bool FromVisitor { get; set; }
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentUtc { get; set; }
    /// <summary>A line from nobody — "Sue Tech has taken over this chat." (a hand-over, 2026-10-15) — drawn as a
    /// note, not as somebody speaking. Stored as a line with no name: every person's line has one.</summary>
    public bool Note { get; set; }
}

/// <summary>
/// Server → console, at sign-in: whether the company has website chat on, the chats waiting or
/// going on now, and the two links a technician can hand a visitor with one click. HelpLink is empty
/// while browser support is switched off.
/// </summary>
public sealed class WebChatListMsg : Msg
{
    public bool Enabled { get; set; }
    public List<WebChatInfo> Chats { get; set; } = new();
    public string AppLink { get; set; } = "";
    public string HelpLink { get; set; } = "";
    /// <summary>This person may hand over a chat somebody ELSE is answering (a supervisor or the owner). Anybody
    /// may give one nobody has answered yet, and whoever is answering may hand over their own.</summary>
    public bool MayHandOverAny { get; set; }
}

/// <summary>Server → console: a chat started, was answered, lost or regained its visitor, or ended.</summary>
public sealed class WebChatUpdateMsg : Msg { public WebChatInfo? Chat { get; set; } }

/// <summary>Server → console: a line in a chat. Error is set only on the sender's copy of a refused line.</summary>
public sealed class WebChatLineMsg : Msg
{
    public WebChatLineInfo? Line { get; set; }
    public int ChatId { get; set; }
    public string Error { get; set; } = "";
}

public sealed class WebChatHistoryRequestMsg : Msg { public int ChatId { get; set; } }
public sealed class WebChatHistoryMsg : Msg { public int ChatId { get; set; } public List<WebChatLineInfo> Lines { get; set; } = new(); }
/// <summary>Console → server: say something to a visitor. The first person to do so takes the chat.</summary>
public sealed class WebChatSayMsg : Msg { public int ChatId { get; set; } public string Text { get; set; } = ""; }
public sealed class WebChatEndMsg : Msg { public int ChatId { get; set; } }
/// <summary>Console → server: give this chat to <see cref="To"/>, somebody of the company signed in now (2026-10-15).
/// A refusal comes back as a <see cref="WebChatLineMsg"/> with Error; the move itself as an update to everybody.</summary>
public sealed class WebChatHandOverMsg : Msg { public int ChatId { get; set; } public string To { get; set; } = ""; }
/// <summary>Console → server: who could take a chat right now — asked when Hand over is pressed.</summary>
public sealed class WebChatPeopleRequestMsg : Msg { public int ChatId { get; set; } }
/// <summary>Server → console: the company's people signed in right now, at a console or on a portal page.</summary>
public sealed class WebChatPeopleMsg : Msg { public int ChatId { get; set; } public List<string> Names { get; set; } = new(); }

// ---------------------------------------------------------------------------
// A look at a machine WITHOUT connecting (2026-10-05): the console's Processes and Performance
// windows. Clint: "so that the technician can see performance before even connecting ... just to
// give one an idea before deciding on connecting or not". His three answers: straight through on
// every machine, attended or unattended, no consent prompt and no unattended password; LOOK ONLY;
// nothing logged and no ticket. Nothing here can change anything on the machine — there is no
// message that ends a process, by decision.
// ---------------------------------------------------------------------------

/// <summary>Technician -> server: open (On) or close a Processes/Performance window on a machine.</summary>
public sealed class WatchMachineMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public bool On { get; set; }
}

/// <summary>
/// Server -> device: send figures (On) or stop. The server sends On when the FIRST technician opens a
/// window on the machine and Off when the last one closes it, so the machine measures once however
/// many are looking, and not at all when nobody is.
/// </summary>
public sealed class WatchRunMsg : Msg { public bool On { get; set; } }

/// <summary>Device -> server -> technicians watching that machine: one reading. The server fills in
/// <see cref="DeviceId"/>; whatever the device put there is ignored.</summary>
public sealed class WatchSampleMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public MachineSample Sample { get; set; } = new();
}

/// <summary>Server -> technician: that machine's figures have stopped, and why — it went offline, its
/// person closed the support app, or it is running a client too old to send them.</summary>
public sealed class WatchEndedMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>One reading of a machine, roughly once a second. The process list rides on every other
/// reading (<see cref="Processes"/> is null on the rest) — it is most of the size.</summary>
public sealed class MachineSample
{
    public DateTimeOffset Ts { get; set; }
    /// <summary>All processors together, 0-100.</summary>
    public double CpuPercent { get; set; }
    public int LogicalProcessors { get; set; }
    public long MemoryUsedMb { get; set; }
    public long MemoryTotalMb { get; set; }
    /// <summary>Task Manager's "active time" across the physical disks, 0-100; -1 when Windows would not say.</summary>
    public double DiskActivePercent { get; set; } = -1;
    public long DiskReadBytesPerSec { get; set; }
    public long DiskWriteBytesPerSec { get; set; }
    public long NetReceiveBytesPerSec { get; set; }
    public long NetSendBytesPerSec { get; set; }
    public long UptimeSeconds { get; set; }
    public int ProcessCount { get; set; }
    public int ThreadCount { get; set; }
    public List<ProcessSample>? Processes { get; set; }
    /// <summary>
    /// The networks Windows says the machine is connected to, each with its Private / Public / Domain
    /// label (Clint, 2026-10-05: "this can solve quite a few problems at a glance" — a network marked
    /// Public turns off file sharing and network discovery, and nothing else on screen says so).
    /// Null from a client that does not report them; empty when connected to none.
    /// </summary>
    public List<NetworkProfile>? Networks { get; set; }
}

/// <summary>One connected network, as Windows' Network and Sharing settings would name it.</summary>
public sealed class NetworkProfile
{
    /// <summary>The network's name — the Wi-Fi's name, or "Network 2" for a cable.</summary>
    public string Name { get; set; } = "";
    /// <summary>"Private", "Public" or "Domain".</summary>
    public string Category { get; set; } = "";
    /// <summary>The adapter it is on, as Windows names it — "Wi-Fi", "Ethernet".</summary>
    public string Adapter { get; set; } = "";
    /// <summary>The machine's address on it (Clint: "it must show i.p address as well") — IPv4, or a
    /// global IPv6 address where there is no IPv4; more than one joined by ", ".</summary>
    public string Address { get; set; } = "";
}

/// <summary>One process, as Task Manager's Details tab would show it.</summary>
public sealed class ProcessSample
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Share of the WHOLE machine, 0-100, as Task Manager shows it.</summary>
    public double CpuPercent { get; set; }
    /// <summary>Private working set — Task Manager's "Memory" column.</summary>
    public long MemoryKb { get; set; }
    /// <summary>The account it runs as; empty when Windows would not say (the tray app cannot see
    /// another account's processes' owners — the agent, as SYSTEM, can).</summary>
    public string User { get; set; } = "";
}

// ---- outside help, step 2 (roadmap 3.9, 2026-10-06): another company connects with a code ----

/// <summary>A technician of ANOTHER company typing the code a customer gave them, in their own console.</summary>
public sealed class OutsiderConnectMsg : Msg { public string Code { get; set; } = ""; }

/// <summary>
/// To every console of the machine's OWN company, the moment somebody from outside starts connecting
/// with a code: who, to which machine, and whose code. The console pops it up with Watch and End —
/// Clint: "the local tech can watch what is going on and terminate the connection if necessary."
/// </summary>
public sealed class OutsiderVisitMsg : Msg
{
    public string SessionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    /// <summary>"Jane Smith (Smith &amp; Co IT)".</summary>
    public string Visitor { get; set; } = "";
    /// <summary>Who the customer said the code was for.</summary>
    public string ForWhom { get; set; } = "";
    /// <summary>Who made the code: the person signed in at the machine.</summary>
    public string MadeBy { get; set; } = "";
    /// <summary>The technician who brought them in from inside the session (§155) — then the pop-up offers Remove,
    /// not End, because the session is that technician's. Empty for the customer's code.</summary>
    public string InvitedBy { get; set; } = "";
}

// ---- outside help asked for by a TECHNICIAN (2026-10-22, §155) ----
//
// Clint: "sometimes it happens that a tech needs the outside assistance rather than the client." In a session,
// Tools → Get outside help… makes a code for THAT session; whoever types it waits for the technician who made it
// to let them in, and the person at the machine is TOLD (Clint's answer), not asked. They leave without ending
// the session; when nobody of the company is left in it, they go too. All on the console's own signed-in socket,
// never the session socket, which is sealed end to end.

/// <summary>A technician in a session asking for a code to bring somebody in. The server checks they are in it.</summary>
public sealed class OutsiderInviteRequestMsg : Msg
{
    public string SessionId { get; set; } = "";
    public string For { get; set; } = "";
}

/// <summary>
/// The answer, and every change after it: the code (sent once — the server keeps only its hash), or why not, or
/// what has become of it. <see cref="State"/> is said in words: "used by …", "… is connected", "… has left".
/// </summary>
public sealed class OutsiderInviteMsg : Msg
{
    public string SessionId { get; set; } = "";
    public string Code { get; set; } = "";
    public string For { get; set; } = "";
    public DateTime? ExpiresUtc { get; set; }
    public string Error { get; set; } = "";
    public string State { get; set; } = "";
    /// <summary>The label of whoever is in the session now with it, so the window can offer Remove. Empty: nobody.</summary>
    public string Connected { get; set; } = "";
}

/// <summary>Stop the technician's live code for this session.</summary>
public sealed class OutsiderInviteCancelMsg : Msg { public string SessionId { get; set; } = ""; }

/// <summary>To the technician who made the code: somebody has typed it and waits to be let in.</summary>
public sealed class OutsiderJoinAskMsg : Msg
{
    public string SessionId { get; set; } = "";
    public string MachineName { get; set; } = "";
    /// <summary>"Jane Smith (Smith &amp; Co IT)", or a guest's "…, a guest — not checked".</summary>
    public string Visitor { get; set; } = "";
    public string ForWhom { get; set; } = "";
    /// <summary>How long the question waits before it is declined by itself.</summary>
    public int Seconds { get; set; }
}

/// <summary>The technician's answer. Files only if they ticked it, for this session only.</summary>
public sealed class OutsiderJoinAnswerMsg : Msg
{
    public string SessionId { get; set; } = "";
    public bool Allow { get; set; }
    public bool Files { get; set; }
}

/// <summary>Take the person a technician brought in out of the session; the session goes on.</summary>
public sealed class OutsiderRemoveMsg : Msg { public string SessionId { get; set; } = ""; }

/// <summary>
/// To the customer's tray app: somebody a technician brought in has joined, or left. TOLD, not asked — Clint,
/// 2026-10-22. An older tray app does not know the message and drops it.
/// </summary>
public sealed class OutsiderJoinedMsg : Msg
{
    public string Visitor { get; set; } = "";
    public string By { get; set; } = "";
    public bool Files { get; set; }
    public bool Left { get; set; }

    /// <summary>The words, here once for the tray app and the harness.</summary>
    public string Describe() => Left
        ? $"{Visitor} has left."
        : $"{Visitor} has joined at {(By.Length > 0 ? By : "the technician")}'s request. They are not from your IT company. "
          + (Files ? "They can see your screen, use the mouse and keyboard, and copy files." : "They can see your screen and use the mouse and keyboard.");
}

/// <summary>
/// What a technician sends the person they are bringing in (§155): the code and both ways in, ready to paste into
/// WhatsApp or an email. Here once, for the console's window and the harness.
///
/// <para>The browser link carries the code after a <c>#</c>: a browser never sends that part to a server, so the code
/// is in no log, and opening the link spends nothing — the page only fills in the box.</para>
/// </summary>
public static class OutsideHelpMessage
{
    /// <summary>The web address for the links, from an app's server address: <c>wss://host/…</c> → <c>https://host</c>. The tray
    /// app knows only its socket's address.</summary>
    public static string WebBase(string serverUrl)
    {
        if (!Uri.TryCreate((serverUrl ?? "").Trim(), UriKind.Absolute, out var u)) return "";
        var scheme = u.Scheme switch { "wss" => "https", "ws" => "http", _ => u.Scheme };
        return $"{scheme}://{u.Authority}";
    }

    public static string BrowserLink(string serverBase, string code) => serverBase.TrimEnd('/') + "/guest/web#" + OutsiderAccess.Clean(code);
    public static string GuestLink(string serverBase) => serverBase.TrimEnd('/') + "/guest";

    public static string Text(string serverBase, string code, string forWhom, string technician, string company)
    {
        var who = technician.Length > 0 && company.Length > 0 ? $"{technician} from {company}" : technician.Length > 0 ? technician : company;
        var hello = forWhom.Trim().Length > 0 ? $"Hello {forWhom.Trim()}," : "Hello,";
        return string.Join("\r\n", new[]
        {
            hello,
            "",
            (who.Length > 0 ? who + " is" : "We are") + " asking for your help on a computer. To connect:",
            "",
            $"Code: {OutsiderAccess.Format(OutsiderAccess.Clean(code))}",
            $"It works once, within {(int)OutsiderAccess.Lifetime.TotalMinutes} minutes.",
            "",
            "In your web browser, nothing to install:",
            BrowserLink(serverBase, code),
            "",
            "Or download the guest program and type the code:",
            GuestLink(serverBase),
            "",
            "If you use The Remote Support Program yourself, choose Connect with a code in your own console.",
            "",
            "You will be let in once you have typed it."
        });
    }

    public static string WhatsAppUrl(string text) => "https://wa.me/?text=" + Uri.EscapeDataString(text);

    public static string MailUrl(string text) =>
        "mailto:?subject=" + Uri.EscapeDataString("A code to connect and help") + "&body=" + Uri.EscapeDataString(text);
}

// ---- outside help, step 3 (roadmap 3.9, 2026-10-06): a GUEST — nobody's account — with a code ----

/// <summary>
/// The guest console (no company, no sign-in) on /ws/guest: the code, and who they say they are. NOT
/// checked — there is nothing to check it against — so everything that shows it says so.
/// </summary>
public sealed class GuestConnectMsg : Msg
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
}

/// <summary>The server's answer to a guest whose code worked: a token for that one session's viewer
/// socket and nothing else, gone when their guest connection closes.</summary>
public sealed class GuestTicketMsg : Msg { public string Token { get; set; } = ""; }

/// <summary>The rules for a guest's name and company, asked by the guest console and the server.</summary>
public static class GuestRules
{
    public const int MaxName = 60, MaxCompany = 80;

    public static string Problem(string? name, string? company)
    {
        var n = (name ?? "").Trim();
        if (n.Length < 2) return "Type your name — the person at the computer is shown it before they decide.";
        if (n.Length > MaxName) return $"Keep your name under {MaxName} characters.";
        if ((company ?? "").Trim().Length > MaxCompany) return $"Keep the company under {MaxCompany} characters.";
        return "";
    }

    /// <summary>How a guest is described everywhere: what they typed, and that nobody has checked it.</summary>
    public static string CompanyLabel(string? company)
    {
        var c = (company ?? "").Trim();
        return c.Length > 0 ? $"{c}, a guest — not checked" : "a guest — not checked";
    }
}

// ---------------------------------------------------------------------------
// Wake-on-LAN (roadmap 1.8, 2026-10-06). A switched-off machine is woken by ANOTHER machine on its
// network — Clint's choice: "via another of their PCs", no router setup at the customer. The server
// picks that machine (the same customer's, on the same network) and hands it MAC addresses only; it
// broadcasts on its own networks and nowhere else. Nothing is logged and no ticket is made.
// ---------------------------------------------------------------------------

/// <summary>Technician -> server: wake this (offline) machine.</summary>
public sealed class WakeRequestMsg : Msg { public string DeviceId { get; set; } = ""; }

/// <summary>Server -> device: send the wake-up for these MACs on your own networks, and say how it went.</summary>
public sealed class WakeSendMsg : Msg
{
    public string RequestId { get; set; } = "";
    /// <summary>The machine being woken, for the record on the sender's side; never used as an address.</summary>
    public string ForMachine { get; set; } = "";
    public List<string> Macs { get; set; } = new();
}

/// <summary>Device -> server: the wake-up went (with how many packets), or why it could not.</summary>
public sealed class WakeSentMsg : Msg
{
    public string RequestId { get; set; } = "";
    public bool Sent { get; set; }
    public int Packets { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>Server -> technician: what became of a wake request, in words — sent from which machine, or why not.</summary>
public sealed class WakeResultMsg : Msg
{
    public string DeviceId { get; set; } = "";
    public bool Sent { get; set; }
    public string Message { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Remote printing (roadmap 1.8, 2026-10-06) — Clint chose BOTH directions.
//
//   Their print -> the technician's printer: the technician switches "Print on my printer" on; the
//   host adds a printer named for them (Windows' own "Microsoft Print to PDF" driver on a file port);
//   each job becomes a PDF the host announces (RemotePrintJobMsg) and the console pulls with the
//   ordinary single-file pull and prints. The printer goes when it is switched off or the session ends.
//
//   The technician's file -> their printer: the console sends the file to %PRINT% with the ordinary
//   single-file send, then asks for it to be printed (PrintFileMsg); a helper running AS THE PERSON
//   prints it on their default printer, and the answer comes back (PrintFileResultMsg).
//
// Both are written on the ticket and shown to the person in the tray app (FileActivityMsg, kinds
// "print-out" and "print-in"). Nobody from outside the company is sent any of these (an allow-list).
// ---------------------------------------------------------------------------

/// <summary>Technician -> host: put a printer for me on this machine (On), or take it away.</summary>
public sealed class RemotePrintMsg : Msg
{
    public string Technician { get; set; } = "";
    public bool On { get; set; }
}

/// <summary>Host -> technicians: where a technician's printer stands on this machine — its name there,
/// or why it could not be added — and jobs that were printed too close together to be caught.</summary>
public sealed class RemotePrintStateMsg : Msg
{
    public string Technician { get; set; } = "";
    public bool On { get; set; }
    /// <summary>What the person sees in their programs' Print window.</summary>
    public string Printer { get; set; } = "";
    public string Error { get; set; } = "";
    public int Lost { get; set; }
}

/// <summary>Host -> technicians: a job printed on a technician's printer is ready to be pulled from
/// <see cref="Path"/> and printed. Only the technician named acts on it.</summary>
public sealed class RemotePrintJobMsg : Msg
{
    public string JobId { get; set; } = "";
    public string Technician { get; set; } = "";
    /// <summary>The document's name as the program that printed it gave it.</summary>
    public string Document { get; set; } = "";
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
}

/// <summary>Technician -> host: print the file already sent to <see cref="Path"/> (under %PRINT%) on the
/// person's default printer.</summary>
public sealed class PrintFileMsg : Msg
{
    public string JobId { get; set; } = "";
    public string Technician { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>The file's own name, for the ticket and for the person.</summary>
    public string Name { get; set; } = "";
}

/// <summary>Host -> technicians: how a <see cref="PrintFileMsg"/> went.</summary>
public sealed class PrintFileResultMsg : Msg
{
    public string JobId { get; set; } = "";
    public bool Ok { get; set; }
    public string Printer { get; set; } = "";
    public int Pages { get; set; }
    public string Error { get; set; } = "";
}
