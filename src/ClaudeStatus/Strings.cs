using System.Globalization;

namespace ClaudeStatus;

/// <summary>
/// User interface text in English and European Portuguese (pt-PT, Microsoft Windows terminology).
/// The language follows the Windows display language (any Portuguese → pt-PT, anything else → English)
/// unless the "Language" policy forces one. Dates and times keep the user's regional format.
/// Every user-visible string goes through here; keep both languages side by side.
/// </summary>
public static class L
{
    /// <summary>True when the interface is in Portuguese. Set once at start-up by <see cref="Init"/>.</summary>
    public static bool Pt { get; private set; }

    /// <summary>The language in use, as a culture name ("en" or "pt-PT").</summary>
    public static string Name => Pt ? "pt-PT" : "en";

    public static void Init(string? policyLanguage = null) => Pt = Resolve(policyLanguage, CultureInfo.CurrentUICulture);

    /// <summary>"en" / "pt-PT" (or "pt") force a language; anything else, or no policy, follows Windows.</summary>
    public static bool Resolve(string? policyLanguage, CultureInfo windows) => policyLanguage?.Trim().ToLowerInvariant() switch
    {
        "en" or "en-us" or "en-gb" => false,
        "pt" or "pt-pt" => true,
        _ => windows.TwoLetterISOLanguageName == "pt",
    };

    static string T(string en, string pt) => Pt ? pt : en;

    // ---- Durations -------------------------------------------------------------------------
    public static string Now => T("now", "agora");
    public static string Days(int d, int h) => $"{d}d {h}h";
    public static string Hours(int h, int m) => T($"{h}h {m:00}m", $"{h}h {m:00}min");
    public static string Minutes(int m) => T($"{m}m", $"{m}min");

    // ---- Limits ----------------------------------------------------------------------------
    public static string LimitLabel(string key) => key switch
    {
        "five_hour" => T("Current session (5h)", "Sessão atual (5h)"),
        "seven_day" => T("Weekly – all models", "Semanal – todos os modelos"),
        "seven_day_opus" => T("Weekly – Opus", "Semanal – Opus"),
        "seven_day_sonnet" => T("Weekly – Sonnet", "Semanal – Sonnet"),
        "extra_usage" => T("Extra usage", "Utilização adicional"),
        _ => key,
    };
    public static string NoData => T("no data", "sem dados");
    public static string Used(double percent) => T($"{percent:0}% used", $"{percent:0}% utilizado");
    public static string ResetsIn(string countdown) => T($"resets in {countdown}", $"reposto dentro de {countdown}");
    public static string Passed(string time, string of) => T($"{time} of {of} passed", $"{time} de {of} decorridos");

    // ---- Forecast --------------------------------------------------------------------------
    public static string ForecastReached(string countdown) =>
        T($"Limit reached – available again in {countdown}.", $"Limite atingido – disponível novamente dentro de {countdown}.");
    public static string ForecastCollecting => T("Collecting data for a forecast…", "A recolher dados para uma previsão…");
    public static string ForecastFlat => T("No recent increase – you are well within this limit.", "Sem aumento recente – está bem dentro deste limite.");
    public static string ForecastHit(DateTimeOffset at, bool withDay, string early) => Pt
        ? $"A este ritmo, atinge o limite por volta das {at:HH:mm}" + (withDay ? $" ({at:ddd})" : "") + $" – {early} antes da reposição."
        : $"At this pace you reach the limit around {at:HH:mm}" + (withDay ? $" on {at:ddd}" : "") + $" – {early} before the reset.";
    public static string ForecastOnTrack(double percent) =>
        T($"On track for about {percent:0}% by the reset.", $"Previsão de cerca de {percent:0}% até à reposição.");

    // ---- Notifications ---------------------------------------------------------------------
    public static string AlertTitle(string key, int level)
    {
        var name = key == "five_hour"
            ? T("Claude session limit (5h)", "Limite de sessão do Claude (5h)")
            : T("Claude weekly limit (7d)", "Limite semanal do Claude (7d)");
        return level >= 100 ? T($"{name} reached", $"{name} atingido") : T($"{name} at {level}%", $"{name} a {level}%");
    }
    public static string Unknown => T("unknown", "desconhecido");
    public static string AlertPaused(string when) =>
        T($"Usage is paused until the limit resets in {when}.", $"A utilização está em pausa até o limite ser reposto, dentro de {when}.");
    public static string AlertUsed(double percent, string when) =>
        T($"{percent:0}% used. Resets in {when}.", $"{percent:0}% utilizado. Reposto dentro de {when}.");
    public static string AccountNotAllowedTitle => T("Claude account not allowed", "Conta Claude não permitida");
    public static string SignInPrompt => T("Click here or the tray icon to sign in to your Claude account.",
        "Clique aqui ou no ícone na área de notificação para iniciar sessão na sua conta Claude.");

    // ---- Errors and status -----------------------------------------------------------------
    public static string ErrUnexpected => T("claude.ai returned an unexpected response. Will retry.", "O claude.ai devolveu uma resposta inesperada. Nova tentativa em breve.");
    public static string ErrNonJson => T("claude.ai returned an unexpected (non-JSON) response. Will retry.", "O claude.ai devolveu uma resposta inesperada (não JSON). Nova tentativa em breve.");
    public static string ErrUnreachable(string detail) => T("Could not reach claude.ai: ", "Não foi possível contactar o claude.ai: ") + detail;
    public static string ErrExpired => T("Session expired – sign in again.", "Sessão expirada – inicie sessão novamente.");
    public static string ErrRejected => T("Session rejected – sign in again.", "Sessão rejeitada – inicie sessão novamente.");
    public static string ErrCloudflare => T("claude.ai is asking for a browser check (Cloudflare). Will retry.", "O claude.ai está a pedir uma verificação do browser (Cloudflare). Nova tentativa em breve.");
    public static string ErrRateLimited => T("Rate limited by claude.ai. Will retry.", "Demasiados pedidos ao claude.ai. Nova tentativa em breve.");
    public static string ErrHttp(int status) => T($"claude.ai returned HTTP {status}.", $"O claude.ai devolveu HTTP {status}.");
    public static string ErrTimeout => T("claude.ai did not answer in time. Will retry.", "O claude.ai não respondeu a tempo. Nova tentativa em breve.");
    public static string NotSignedIn => T("Not signed in.", "Sessão não iniciada.");
    public static string Loading => T("Loading…", "A carregar…");
    public static string AccountRemoved(string label) =>
        T($"{label} is not allowed by your administrator and was removed.", $"A conta {label} não é permitida pelo administrador e foi removida.");
    public static string AccountsRemoved(string labels) =>
        T($"{labels} are not allowed by your administrator and were removed.", $"As contas {labels} não são permitidas pelo administrador e foram removidas.");
    public static string EmailNotAllowed(string? email) => email is null
        ? T("This account is not allowed by your administrator.", "Esta conta não é permitida pelo administrador.")
        : T($"{email} is not allowed by your administrator.", $"A conta {email} não é permitida pelo administrador.");
    public static string NoOrganizations => T("This account has no Claude organizations.", "Esta conta não tem organizações Claude.");
    public static string NoOrganizationAllowed => T("None of this account's organizations are allowed by your administrator.",
        "Nenhuma das organizações desta conta é permitida pelo administrador.");
    public static string AccountN(int n) => T($"Account {n}", $"Conta {n}");
    public static string NetworkWindowTitle => T("Claude Status (network)", "Claude Status (rede)");

    // ---- Taskbar widget --------------------------------------------------------------------
    public static string WidgetSignIn => T("Claude · sign in", "Claude · iniciar sessão");
    public static string WidgetOffline => T("Claude · offline", "Claude · sem ligação");
    public static string LastUpdate(DateTimeOffset at, string? message) =>
        T($"Last update {at:t} – {message}", $"Última atualização às {at:t} – {message}");
    public static string Updated(DateTimeOffset at, string format) =>
        T("Updated ", "Atualizado às ") + at.ToString(format, CultureInfo.CurrentCulture);

    // ---- Tray menu and icons ---------------------------------------------------------------
    public static string Quit => T("Quit Claude Status", "Sair do Claude Status");
    public static string QuitBlocked => T("Quit Claude Status (not allowed by administrator)", "Sair do Claude Status (não permitido pelo administrador)");
    public static string Display => T("Display", "Mostrar");
    public static string TaskbarWidget => T("Taskbar widget", "Widget na barra de tarefas");
    public static string SessionIcon => T("5-hour tray icon", "Ícone de 5 horas");
    public static string WeeklyIcon => T("7-day tray icon", "Ícone de 7 dias");
    public static string WidgetTwoRows => T("Widget: two rows", "Widget: duas linhas");
    public static string WidgetOneLine => T("Widget: one line", "Widget: uma linha");
    public static string ResetWidgetPosition => T("Reset widget position", "Repor a posição do widget");
    public static string RefreshEvery => T("Refresh every", "Atualizar a cada");
    public static string Notifications => T("Notifications", "Notificações");
    public static string History => T("Usage history (Stats)", "Histórico de utilização (Estatísticas)");
    public static string HistoryBlocked => T("Usage history (off by administrator)", "Histórico de utilização (desativado pelo administrador)");
    public static string HistoryOffConfirm => T("Turn off usage history? The history stored on this PC for all accounts is deleted.",
        "Desativar o histórico de utilização? O histórico guardado neste PC para todas as contas será eliminado.");
    public static string StartWithWindows => T("Start with Windows", "Iniciar com o Windows");
    public static string StartWithWindowsByAdmin => T("Start with Windows (set by administrator)", "Iniciar com o Windows (definido pelo administrador)");
    public static string Account => T("Account", "Conta");
    public static string AccountNamed(string label) => T($"Account: {label}", $"Conta: {label}");
    public static string Organization => T("Organization", "Organização");
    public static string SignIn => T("Sign in…", "Iniciar sessão…");
    public static string SignInAgain => T("Sign in again…", "Iniciar sessão novamente…");
    public static string AddAccount => T("Add another account…", "Adicionar outra conta…");
    public static string SignOutOf(string label) => T($"Sign out of {label}", $"Terminar sessão de {label}");
    public static string ShowDetails => T("Show details", "Mostrar detalhes");
    public static string RefreshNow => T("Refresh now", "Atualizar agora");
    public static string OpenUsagePage => T("Open usage page", "Abrir a página de utilização");
    public static string AboutMenu(string version) => T($"About Claude Status ({version})", $"Acerca do Claude Status ({version})");
    public static string OneMustStayOn => T("At least one must stay on.", "Pelo menos um tem de ficar ativo.");
    public static string SessionIconName => T("Claude session (5h)", "Sessão do Claude (5h)");
    public static string WeeklyIconName => T("Claude weekly (7d)", "Claude semanal (7d)");

    // ---- Details window --------------------------------------------------------------------
    public static string UsageTitle => T("Claude usage", "Utilização do Claude");
    public static string Refresh => T("Refresh", "Atualizar");
    public static string UsagePage => T("Usage page", "Página de utilização");
    public static string AccountMenu => T("Account ▾", "Conta ▾");
    public static string QuitShort => T("Quit", "Sair");
    public static string TabNow => T("Now", "Agora");
    public static string TabStats => T("Stats", "Estatísticas");
    public static string OrganizationNamed(string name) => T("Organization: ", "Organização: ") + name;
    public static string NoDataYet => T("No data yet.", "Ainda sem dados.");
    public static string CsvFilter => T("CSV file (*.csv)|*.csv", "Ficheiro CSV (*.csv)|*.csv");
    public static string SaveFailed(string detail) => T("The file could not be saved: ", "Não foi possível guardar o ficheiro: ") + detail;
    public static string ClearHistoryConfirm => T("Delete the usage history of this account and organization?",
        "Eliminar o histórico de utilização desta conta e organização?");

    // ---- Stats -----------------------------------------------------------------------------
    public static string NoHistory => T("No history yet. It builds up while Claude Status runs.", "Ainda sem histórico. É criado enquanto o Claude Status está em execução.");
    public static string Range14Days => T("14 days", "14 dias");
    public static string RangeMonth => T("Month", "Mês");
    public static string RangeYear => T("Year", "Ano");
    public static string WhenYouUse => T("When you use Claude", "Quando utiliza o Claude");
    public static string ExportCsv => T("Export CSV…", "Exportar CSV…");
    public static string ClearHistory => T("Clear history", "Limpar histórico");
    public static string HourAdded(string label, int hour, double percent) =>
        T($"{label} {hour:00}:00 – {percent:0.#}% added", $"{label} {hour:00}:00 – {percent:0.#}% adicionado");
    public static string DayNoData(string date) => T($"{date}: no data", $"{date}: sem dados");
    public static string DaySessionPeak(string date, double peak, int hits) =>
        T($"{date}: session peak {peak:0}%", $"{date}: pico da sessão {peak:0}%") +
        (hits > 0 ? T($", limit hit {hits}×", $", limite atingido {hits}×") : "");
    public static string Chart14Title => T("Session peak per day – last 14 days", "Pico da sessão por dia – últimos 14 dias");
    public static string Chart14Note => T("Highest 5-hour session usage each day. Red = limit reached.",
        "Utilização mais alta da sessão de 5 horas em cada dia. Vermelho = limite atingido.");
    public static string SessionLimitsHit => T("session limits hit", "limites de sessão atingidos");
    public static string AverageDailyPeak => T("average daily peak", "pico diário médio");
    public static string LongestStreak => T("longest streak without a limit", "maior sequência sem atingir o limite");
    public static string ChartMonthTitle(string month) => T("Session peak per day – ", "Pico da sessão por dia – ") + month;
    public static string ChartMonthNote => T("Calendar month; days still to come are empty.", "Mês civil; os dias ainda por vir estão vazios.");
    public static string SessionLimitsHitMonth => T("session limits hit this month", "limites de sessão atingidos este mês");
    public static string AverageWeeklyUsage => T("average weekly usage", "utilização semanal média");
    public static string MonthNoData(string name) => T($"{name}: no data", $"{name}: sem dados");
    public static string MonthSummary(string name, double avg, int hits) =>
        T($"{name}: average weekly peak {avg:0}%, session limit hit {hits}×", $"{name}: pico semanal médio {avg:0}%, limite de sessão atingido {hits}×");
    public static string ChartYearTitle => T("Average weekly peak per month – last 12 months", "Pico semanal médio por mês – últimos 12 meses");
    public static string ChartYearNote => T("Average of each week's highest weekly usage; amber/red = close to or over the weekly limit.",
        "Média da utilização semanal mais alta de cada semana; âmbar/vermelho = perto ou acima do limite semanal.");
    public static string SessionLimitsHitYear => T("session limits hit in 12 months", "limites de sessão atingidos em 12 meses");
    public static string BusiestMonth => T("busiest month", "mês com mais utilização");
    public static string DayCount(int n) => n == 1 ? T("1 day", "1 dia") : T($"{n} days", $"{n} dias");

    // ---- About -----------------------------------------------------------------------------
    public static string AboutTitle => T("About Claude Status", "Acerca do Claude Status");
    public static string AboutTagline => T("Claude plan usage in the Windows taskbar and notification area.",
        "Utilização do plano Claude na barra de tarefas e na área de notificação do Windows.");
    public static string VersionLine(string version) => T($"Version {version}", $"Versão {version}");
    public static string BuildSuffix(string commit) => T($"  (build {commit})", $"  (compilação {commit})");
    public static string ProjectPage => T("Project page and releases", "Página do projeto e versões");
    public static string LicenseLink => T("License (MIT)", "Licença (MIT)");
    public static string OpenSource => T("Open source under the MIT License.", "Código aberto ao abrigo da Licença MIT.");
    public static string NotAffiliated => T("Not affiliated with or endorsed by Anthropic.", "Sem afiliação nem aprovação da Anthropic.");
    public static string CopyVersion => T("Copy version", "Copiar versão");

    // ---- Sign-in window --------------------------------------------------------------------
    public static string SignInTitle => T("Sign in to Claude", "Iniciar sessão no Claude");
    public static string WebView2Required => T("Microsoft Edge WebView2 Runtime is required.", "É necessário o Microsoft Edge WebView2 Runtime.");
}
