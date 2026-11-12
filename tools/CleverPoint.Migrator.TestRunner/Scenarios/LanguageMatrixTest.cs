using System.Globalization;
using System.IO.Compression;
using System.Text;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Multilingual copy matrix. A user reported that copying from a German site ("Dokumente") into an
/// English one ("Documents") created the folders but no files, with "list Dokumente is not present".
/// SharePoint localizes the titles of built-in lists per site language AND per request language, so
/// anything that finds a list by display title is fragile across languages.
///
///   lang-provision  creates source and target subsites in several languages and seeds them with
///                   localized folders, files, a custom library and a custom list.
///   lang-probe      prints what each site calls its built-in libraries over REST and over CSOM
///                   under different UI cultures (the machine language of the person running the app).
///   lang-copy       runs the copies across language pairs and UI cultures and verifies the result.
/// </summary>
public static class LanguageMatrixTest
{
    private sealed record Pack(
        string Code, int Lcid, string LibTitle, string ListTitle,
        string[] Folders, string[] Files, string[] LibFiles,
        string ColCustomer, string ColStatus, string ColAmount, string ColDue,
        string[] Statuses, string[] ItemTitles, string[] ListFolders, string Paragraph);

    private const string LibUrl = "LangProjectLib";
    private const string ListUrl = "Lists/LangTasks";

    private static readonly Pack[] SourcePacks =
    {
        new("de", 1031, "Projektunterlagen Köln", "Aufgaben und Maßnahmen",
            new[] { "Verträge", "Verträge/2024", "Verträge/2024/Entwürfe", "Rechnungen", "Rechnungen/Müller GmbH", "Personal/Bewerbungen" },
            new[] { "Übersicht.docx", "Verträge/Rahmenvertrag Müller.docx", "Verträge/2024/Änderungsvereinbarung.docx",
                    "Verträge/2024/Entwürfe/Entwurf für Prüfung.txt", "Rechnungen/Offene Posten.csv",
                    "Rechnungen/Müller GmbH/Rechnung 2024-0815.docx", "Personal/Bewerbungen/Lebenslauf Jörg Weiß.docx", "Größenänderung & Maße.md" },
            new[] { "Besprechungsprotokoll.docx", "Zeitplan/Meilensteine.csv", "Zeitplan/Ausführung/Bauabschnitt Süd.txt" },
            "Kunde", "Status", "Betrag", "Fällig am",
            new[] { "Offen", "In Arbeit", "Erledigt" },
            new[] { "Angebot prüfen", "Vertrag unterschreiben", "Rechnung überweisen", "Rückfrage klären", "Lieferung bestätigen", "Mängel beheben" },
            new[] { "Dringend", "Rückstellung" },
            "Dieses Dokument fasst die Ergebnisse der Besprechung zusammen. Nächste Schritte: Prüfung der Verträge, Klärung offener Posten und Übergabe an die Buchhaltung."),
        new("fr", 1036, "Dossiers de projet", "Tâches et échéances",
            new[] { "Contrats", "Contrats/2024", "Contrats/2024/Brouillons", "Factures", "Factures/Société Générale", "Ressources humaines/Candidatures" },
            new[] { "Aperçu général.docx", "Contrats/Contrat-cadre Lefèvre.docx", "Contrats/2024/Avenant n°2.docx",
                    "Contrats/2024/Brouillons/Brouillon à relire.txt", "Factures/Éléments en attente.csv",
                    "Factures/Société Générale/Facture 2024-0815.docx", "Ressources humaines/Candidatures/CV Hélène Dupré.docx", "Synthèse d'été.md" },
            new[] { "Compte rendu de réunion.docx", "Planning/Jalons.csv", "Planning/Exécution/Lot façade.txt" },
            "Client", "État", "Montant", "Échéance",
            new[] { "Ouvert", "En cours", "Terminé" },
            new[] { "Vérifier l'offre", "Signer le contrat", "Régler la facture", "Répondre au client", "Confirmer la livraison", "Corriger les défauts" },
            new[] { "Urgent", "Différé" },
            "Ce document résume les conclusions de la réunion. Prochaines étapes : vérification des contrats, traitement des éléments en attente et transmission à la comptabilité."),
        new("es", 3082, "Documentación del proyecto", "Tareas y vencimientos",
            new[] { "Contratos", "Contratos/2024", "Contratos/2024/Borradores", "Facturas", "Facturas/Muñoz e Hijos", "Recursos humanos/Candidaturas" },
            new[] { "Resumen general.docx", "Contratos/Contrato marco Núñez.docx", "Contratos/2024/Modificación número 2.docx",
                    "Contratos/2024/Borradores/Borrador para revisión.txt", "Facturas/Partidas pendientes.csv",
                    "Facturas/Muñoz e Hijos/Factura 2024-0815.docx", "Recursos humanos/Candidaturas/Currículum Íñigo Peña.docx", "¿Qué falta por hacer.md" },
            new[] { "Acta de la reunión.docx", "Planificación/Hitos.csv", "Planificación/Ejecución/Sección sur.txt" },
            "Cliente", "Estado", "Importe", "Fecha de vencimiento",
            new[] { "Abierto", "En curso", "Terminado" },
            new[] { "Revisar la oferta", "Firmar el contrato", "Pagar la factura", "Aclarar la consulta", "Confirmar la entrega", "Corregir los defectos" },
            new[] { "Urgente", "Aplazado" },
            "Este documento resume las conclusiones de la reunión. Próximos pasos: revisión de los contratos, aclaración de las partidas pendientes y entrega a contabilidad."),
        new("ja", 1041, "プロジェクト資料", "タスクと期限",
            new[] { "契約書", "契約書/2024年", "契約書/2024年/下書き", "請求書", "請求書/山田商事", "人事/応募書類" },
            new[] { "概要.docx", "契約書/基本契約書 山田.docx", "契約書/2024年/変更合意書.docx",
                    "契約書/2024年/下書き/確認用の下書き.txt", "請求書/未処理項目.csv",
                    "請求書/山田商事/請求書 2024-0815.docx", "人事/応募書類/履歴書 佐藤 花子.docx", "メモ（重要）.md" },
            new[] { "議事録.docx", "スケジュール/マイルストーン.csv", "スケジュール/実施/南工区.txt" },
            "顧客", "状態", "金額", "期限",
            new[] { "未着手", "進行中", "完了" },
            new[] { "見積書を確認する", "契約書に署名する", "請求書を支払う", "問い合わせに回答する", "納品を確認する", "不具合を修正する" },
            new[] { "至急", "保留" },
            "この文書は会議の結果をまとめたものです。次のステップ：契約書の確認、未処理項目の整理、経理部への引き継ぎ。"),
        new("ru", 1049, "Проектная документация", "Задачи и сроки",
            new[] { "Договоры", "Договоры/2024", "Договоры/2024/Черновики", "Счета", "Счета/ООО Ромашка", "Кадры/Резюме" },
            new[] { "Обзор.docx", "Договоры/Рамочный договор Ёлкин.docx", "Договоры/2024/Дополнительное соглашение №2.docx",
                    "Договоры/2024/Черновики/Черновик на проверку.txt", "Счета/Открытые позиции.csv",
                    "Счета/ООО Ромашка/Счёт 2024-0815.docx", "Кадры/Резюме/Резюме Щукин Пётр.docx", "Что ещё сделать.md" },
            new[] { "Протокол совещания.docx", "График/Этапы.csv", "График/Выполнение/Южный участок.txt" },
            "Клиент", "Состояние", "Сумма", "Срок",
            new[] { "Открыто", "В работе", "Завершено" },
            new[] { "Проверить предложение", "Подписать договор", "Оплатить счёт", "Уточнить вопрос", "Подтвердить поставку", "Устранить недостатки" },
            new[] { "Срочно", "Отложено" },
            "Этот документ подводит итоги совещания. Следующие шаги: проверка договоров, разбор открытых позиций и передача в бухгалтерию."),
        new("en", 1033, "Project Files", "Tasks and Deadlines",
            new[] { "Contracts", "Contracts/2024", "Contracts/2024/Drafts", "Invoices", "Invoices/Miller & Sons", "Human Resources/Applications" },
            new[] { "Overview.docx", "Contracts/Master Agreement Miller.docx", "Contracts/2024/Amendment No 2.docx",
                    "Contracts/2024/Drafts/Draft for review.txt", "Invoices/Open items.csv",
                    "Invoices/Miller & Sons/Invoice 2024-0815.docx", "Human Resources/Applications/Resume John O'Neil.docx", "What's left to do.md" },
            new[] { "Meeting minutes.docx", "Schedule/Milestones.csv", "Schedule/Execution/South section.txt" },
            "Customer", "Status", "Amount", "Due date",
            new[] { "Open", "In progress", "Done" },
            new[] { "Review the offer", "Sign the contract", "Pay the invoice", "Answer the inquiry", "Confirm delivery", "Fix the defects" },
            new[] { "Urgent", "Deferred" },
            "This document summarizes the outcome of the meeting. Next steps: review the contracts, clear the open items and hand over to accounting."),
    };

    private static readonly (string Code, int Lcid)[] TargetLangs = { ("en", 1033), ("de", 1031), ("fr", 1036), ("ja", 1041) };

    /// <summary>source language -> target language. de->en is the reported case.</summary>
    private static readonly (string Src, string Tgt)[] Pairs =
    {
        ("de", "en"), ("en", "de"), ("fr", "ja"), ("ja", "fr"), ("es", "de"), ("ru", "en"), ("de", "de"),
    };

    /// <summary>
    /// The display language of the person running the copy. "" = whatever the site itself uses (what an
    /// app-only connection gets). A signed-in user gets built-in titles in THEIR language instead.
    /// </summary>
    private static readonly string[] UserLanguages = { "", "de-DE", "en-US", "ja-JP" };

    private static string SrcUrl(string code) => Program.Source.SiteUrl.TrimEnd('/') + "/lang-src-" + code;
    // Targets sit beside the sources: the lab tenant has subsite creation switched off.
    private static SpConnection TargetParent => Program.Source;
    private static string TgtUrl(string code) => TargetParent.SiteUrl.TrimEnd('/') + "/lang-tgt-" + code;

    // ------------------------------------------------------------------ provision

    public static async Task ProvisionAsync()
    {
        foreach (var p in SourcePacks)
        {
            var site = await EnsureWebAsync(Program.Source, "lang-src-" + p.Code, p.Lcid, $"Language test source ({p.Code})");
            if (site is null) continue;
            try
            {
                await SeedAsync(site, p);
                Program.Check($"lang-provision: source {p.Code} seeded", true, site.SiteUrl);
            }
            catch (Exception ex) { Program.Check($"lang-provision: source {p.Code} seeded", false, ex.Message); }
        }
        foreach (var (code, lcid) in TargetLangs)
            await EnsureWebAsync(TargetParent, "lang-tgt-" + code, lcid, $"Language test target ({code})");
    }

    private static async Task<SpConnection?> EnsureWebAsync(SpConnection parent, string leaf, int lcid, string title)
    {
        var url = parent.SiteUrl.TrimEnd('/') + "/" + leaf;
        try
        {
            using var ctx = parent.CreateContext();
            var webs = ctx.Web.Webs;
            ctx.Load(webs, ws => ws.Include(w => w.Url, w => w.Language));
            await ctx.ExecuteWithRetryAsync();
            var have = webs.AsEnumerable().FirstOrDefault(w => w.Url.TrimEnd('/').EndsWith("/" + leaf, StringComparison.OrdinalIgnoreCase));
            if (have is null)
            {
                var web = ctx.Web.Webs.Add(new WebCreationInformation
                {
                    Url = leaf, Title = title, WebTemplate = "STS#3", Language = lcid, UseSamePermissionsAsParentSite = true,
                });
                ctx.Load(web, w => w.Language);
                await ctx.ExecuteWithRetryAsync();
                Program.Check($"lang-provision: created {leaf}", web.Language == (uint)lcid, $"{url} language={web.Language}");
            }
            else
                Program.Check($"lang-provision: {leaf} exists", have.Language == (uint)lcid, $"{url} language={have.Language}");
            return parent.ForWeb(url);
        }
        catch (Exception ex)
        {
            Program.Check($"lang-provision: {leaf}", false, ex.Message);
            return null;
        }
    }

    private static async Task SeedAsync(SpConnection site, Pack p)
    {
        using var ctx = site.CreateContext();

        // 1. The built-in document library ("Dokumente", "Documents", ...).
        var docs = ctx.Web.DefaultDocumentLibrary();
        ctx.Load(docs, l => l.Id);
        ctx.Load(docs.RootFolder, f => f.ServerRelativeUrl);
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Id, l => l.RootFolder));
        await ctx.ExecuteWithRetryAsync();
        await SeedFilesAsync(ctx, docs.RootFolder.ServerRelativeUrl, p.Folders, p.Files, p);

        bool Has(string urlTail) => lists.AsEnumerable().Any(l =>
            l.RootFolder.ServerRelativeUrl.EndsWith("/" + urlTail, StringComparison.OrdinalIgnoreCase));

        // 2. A custom library with a localized title.
        if (!Has(LibUrl))
        {
            var lib = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = p.LibTitle, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = LibUrl,
            });
            lib.Fields.AddFieldAsXml($"<Field Type='Text' Name='LangCustomer' DisplayName='{Xml(p.ColCustomer)}' MaxLength='100' />",
                true, AddFieldOptions.AddFieldInternalNameHint);
            await ctx.ExecuteWithRetryAsync();
        }
        var libRoot = ctx.Web.ServerRelativeUrlOf(LibUrl, docs.RootFolder.ServerRelativeUrl);
        var libFolders = p.LibFiles.Where(f => f.Contains('/')).Select(f => f[..f.LastIndexOf('/')])
            .SelectMany(Ancestors).Distinct().ToArray();
        await SeedFilesAsync(ctx, libRoot, libFolders, p.LibFiles, p);

        // 3. A custom list with localized title, column names, choices, folders and items.
        if (!Has(ListUrl))
        {
            var list = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = p.ListTitle, TemplateType = (int)ListTemplateType.GenericList, Url = ListUrl,
            });
            list.EnableFolderCreation = true;
            list.Update();
            var choices = string.Concat(p.Statuses.Select(s => $"<CHOICE>{Xml(s)}</CHOICE>"));
            foreach (var xml in new[]
            {
                $"<Field Type='Text' Name='LangCustomer' DisplayName='{Xml(p.ColCustomer)}' MaxLength='100' />",
                $"<Field Type='Choice' Name='LangStatus' DisplayName='{Xml(p.ColStatus)}'><CHOICES>{choices}</CHOICES></Field>",
                $"<Field Type='Number' Name='LangAmount' DisplayName='{Xml(p.ColAmount)}' Decimals='2' />",
                $"<Field Type='DateTime' Name='LangDue' DisplayName='{Xml(p.ColDue)}' Format='DateOnly' />",
            })
                list.Fields.AddFieldAsXml(xml, true, AddFieldOptions.AddFieldInternalNameHint);
            await ctx.ExecuteWithRetryAsync();

            ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteWithRetryAsync();
            var root = list.RootFolder.ServerRelativeUrl;
            foreach (var folder in p.ListFolders)
            {
                var fi = list.AddItem(new ListItemCreationInformation { UnderlyingObjectType = FileSystemObjectType.Folder, LeafName = folder });
                fi["Title"] = folder;
                fi.Update();
            }
            await ctx.ExecuteWithRetryAsync();

            var customer = p.Folders.Last().Split('/')[0];
            for (var i = 0; i < p.ItemTitles.Length * 2; i++)
            {
                // First pass at the root, second pass spread over the folders.
                var info = new ListItemCreationInformation();
                if (i >= p.ItemTitles.Length) info.FolderUrl = $"{root}/{p.ListFolders[i % p.ListFolders.Length]}";
                var item = list.AddItem(info);
                item["Title"] = p.ItemTitles[i % p.ItemTitles.Length] + (i >= p.ItemTitles.Length ? " (2)" : "");
                item["LangCustomer"] = $"{customer} {i + 1}";
                item["LangStatus"] = p.Statuses[i % p.Statuses.Length];
                item["LangAmount"] = 1250.5 + i * 317.25;
                item["LangDue"] = new DateTime(2025, 1 + i % 12, 10 + i, 12, 0, 0, DateTimeKind.Utc);
                item.Update();
            }
            await ctx.ExecuteWithRetryAsync();
        }
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++) yield return string.Join('/', parts[..i]);
    }

    private static string ServerRelativeUrlOf(this Web _, string leaf, string siblingRoot) =>
        siblingRoot[..siblingRoot.LastIndexOf('/')] + "/" + leaf;

    private static async Task SeedFilesAsync(ClientContext ctx, string root, string[] folders, string[] files, Pack p)
    {
        foreach (var path in folders.SelectMany(Ancestors).Distinct())
        {
            ctx.Web.Folders.Add($"{root}/{path}");
            await ctx.ExecuteWithRetryAsync();
        }
        var n = 0;
        foreach (var path in files)
        {
            n++;
            var slash = path.LastIndexOf('/');
            var folderUrl = slash < 0 ? root : $"{root}/{path[..slash]}";
            var name = slash < 0 ? path : path[(slash + 1)..];
            var bytes = Content(name, p, n);
            var folder = ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(folderUrl));
            folder.Files.Add(new FileCreationInformation { Url = name, ContentStream = new MemoryStream(bytes), Overwrite = true });
            await ctx.ExecuteWithRetryAsync();
        }
    }

    private static byte[] Content(string name, Pack p, int n)
    {
        var title = Path.GetFileNameWithoutExtension(name);
        switch (Path.GetExtension(name).ToLowerInvariant())
        {
            case ".docx": return Docx(title, Enumerable.Repeat(p.Paragraph, 3 + n % 4).ToArray());
            case ".csv":
            {
                var sb = new StringBuilder($"{p.ColCustomer};{p.ColStatus};{p.ColAmount};{p.ColDue}\r\n");
                for (var i = 0; i < 40; i++)
                    sb.Append($"{p.ItemTitles[i % p.ItemTitles.Length]};{p.Statuses[i % p.Statuses.Length]};{1250.5 + i * 317.25:F2};2025-{1 + i % 12:D2}-{1 + i % 27:D2}\r\n");
                return new UTF8Encoding(true).GetBytes(sb.ToString());
            }
            case ".md": return new UTF8Encoding(false).GetBytes($"# {title}\n\n{p.Paragraph}\n\n" + string.Concat(p.ItemTitles.Select(t => $"- {t}\n")));
            default: return new UTF8Encoding(true).GetBytes($"{title}\r\n\r\n{p.Paragraph}\r\n");
        }
    }

    /// <summary>A minimal but valid Word document, so the mock files open like real ones.</summary>
    private static byte[] Docx(string title, string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Part(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            Part("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Part("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            var body = new StringBuilder();
            body.Append($"<w:p><w:r><w:rPr><w:b/><w:sz w:val=\"36\"/></w:rPr><w:t>{Xml(title)}</w:t></w:r></w:p>");
            foreach (var para in paragraphs) body.Append($"<w:p><w:r><w:t xml:space=\"preserve\">{Xml(para)}</w:t></w:r></w:p>");
            Part("word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                body + "</w:body></w:document>");
        }
        return ms.ToArray();
    }

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    // ------------------------------------------------------------------ probe

    /// <summary>What the explorer (REST) and the engine (CSOM) each call a site's lists.</summary>
    public static async Task ProbeAsync()
    {
        var sites = SourcePacks.Select(p => (Name: "src-" + p.Code, Conn: Program.Source.ForWeb(SrcUrl(p.Code))))
            .Concat(TargetLangs.Select(t => (Name: "tgt-" + t.Code, Conn: TargetParent.ForWeb(TgtUrl(t.Code)))));
        foreach (var (name, conn) in sites)
        {
            try
            {
                var rest = await RestListsAsync(conn);
                Console.WriteLine($"  {name}: REST (as the explorer sees it)");
                foreach (var l in rest) Console.WriteLine($"      [{l.Template}] '{l.Title}'  ->  {l.RootUrl}");
                foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "ja-JP" })
                {
                    using var _ = UseCulture(culture);
                    using var ctx = conn.CreateContext();
                    var lists = ctx.Web.Lists;
                    ctx.Load(lists, ls => ls.Include(l => l.Id, l => l.Title, l => l.Hidden));
                    await ctx.ExecuteWithRetryAsync();
                    var csom = lists.AsEnumerable().ToDictionary(l => l.Id, l => l.Title);
                    var differs = rest.Where(r => csom.TryGetValue(r.Id, out var t) && t != r.Title)
                        .Select(r => $"'{r.Title}' is '{csom[r.Id]}'").ToList();

                    // Can CSOM find each list by the title REST reported?
                    var missing = new List<string>();
                    foreach (var r in rest)
                    {
                        try
                        {
                            var l = ctx.Web.Lists.GetByTitle(r.Title);
                            ctx.Load(l, x => x.Id);
                            await ctx.ExecuteWithRetryAsync();
                        }
                        catch (ServerException) { missing.Add(r.Title); }
                    }
                    Console.WriteLine($"      CSOM under {culture}: {(differs.Count == 0 ? "same titles as REST" : string.Join("; ", differs))}" +
                        (missing.Count == 0 ? "" : $"  | GetByTitle FAILS for: {string.Join(", ", missing)}"));
                    Program.Check($"lang-probe: {name} titles agree and resolve under {culture}", differs.Count == 0 && missing.Count == 0,
                        differs.Count + missing.Count == 0 ? null : $"{differs.Count} title(s) differ, {missing.Count} not found by title");
                }
            }
            catch (Exception ex) { Program.Check($"lang-probe: {name}", false, ex.Message); }
        }
    }

    /// <summary>
    /// Does the REQUEST language change the titles a site reports, and what GetByTitle matches?
    /// Every SharePoint Online site has all alternate languages enabled, so a signed-in user whose
    /// profile language differs from the site language sees translated built-in titles.
    /// </summary>
    public static async Task HeaderProbeAsync()
    {
        foreach (var (name, url) in new[] { ("tgt-en", TgtUrl("en")), ("src-de", SrcUrl("de")), ("tgt-ja", TgtUrl("ja")) })
        {
            var conn = Program.Source.ForWeb(url);
            foreach (var lang in new[] { "(none)", "en-US", "de-DE", "fr-FR" })
            {
                var headers = lang == "(none)" ? null : new Dictionary<string, string> { ["Accept-Language"] = lang };
                var json = await conn.Rest.SendAsync(HttpMethod.Get,
                    $"{conn.SiteUrl}/_api/web/lists?$select=Title,BaseTemplate&$filter=Hidden eq false", null, headers);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var titles = doc.RootElement.GetProperty("value").EnumerateArray()
                    .Where(e => e.GetProperty("BaseTemplate").GetInt32() is 101 or 119)
                    .Select(e => e.GetProperty("Title").GetString()).ToList();
                var found = new List<string>();
                foreach (var t in new[] { "Documents", "Dokumente", "ドキュメント" })
                {
                    try
                    {
                        await conn.Rest.SendAsync(HttpMethod.Get,
                            $"{conn.SiteUrl}/_api/web/lists/getbytitle('{Uri.EscapeDataString(t)}')?$select=Id", null, headers);
                        found.Add(t);
                    }
                    catch (Exception) { }
                }
                Console.WriteLine($"  {name} Accept-Language {lang,-7}: titles [{string.Join(", ", titles)}]  getbytitle finds [{string.Join(", ", found)}]");
            }
        }
    }

    private sealed record RestList(Guid Id, string Title, int Template, string RootUrl);

    /// <summary>The same query the explorer uses to list a site's lists.</summary>
    private static async Task<List<RestList>> RestListsAsync(SpConnection conn)
    {
        using var doc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists?$select=Id,Title,Hidden,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false&$top=500");
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(e => new RestList(
            e.GetProperty("Id").GetGuid(), e.GetProperty("Title").GetString() ?? "", e.GetProperty("BaseTemplate").GetInt32(),
            e.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString() ?? "")).ToList();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _ui = CultureInfo.CurrentUICulture, _c = CultureInfo.CurrentCulture;
        private readonly CultureInfo? _dui = CultureInfo.DefaultThreadCurrentUICulture, _dc = CultureInfo.DefaultThreadCurrentCulture;
        public CultureScope(string name)
        {
            var c = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = c; CultureInfo.CurrentCulture = c;
            CultureInfo.DefaultThreadCurrentUICulture = c; CultureInfo.DefaultThreadCurrentCulture = c;
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = _ui; CultureInfo.CurrentCulture = _c;
            CultureInfo.DefaultThreadCurrentUICulture = _dui; CultureInfo.DefaultThreadCurrentCulture = _dc;
        }
    }

    private sealed class LanguageScope : IDisposable
    {
        private readonly string? _previous = Core.Http.SpRestClient.RequestLanguage;
        public LanguageScope(string language) => Core.Http.SpRestClient.RequestLanguage = language.Length == 0 ? null : language;
        public void Dispose() => Core.Http.SpRestClient.RequestLanguage = _previous;
    }

    /// <summary>Makes every request ask for this display language, like a signed-in user with that profile language.</summary>
    private static IDisposable UseRequestLanguage(string language) => new LanguageScope(language);

    /// <summary>Pretends the app runs on a machine with this display language.</summary>
    private static IDisposable UseCulture(string name) => new CultureScope(name);

    // ------------------------------------------------------------------ copy

    public static async Task CopyAsync()
    {
        foreach (var (srcCode, tgtCode) in Pairs)
        {
            var src = Program.Source.ForWeb(SrcUrl(srcCode));
            var tgt = TargetParent.ForWeb(TgtUrl(tgtCode));
            var pack = SourcePacks.First(p => p.Code == srcCode);
            var only = Environment.GetEnvironmentVariable("LANG_ONLY");
            if (!string.IsNullOrEmpty(only) && only != $"{srcCode}->{tgtCode}") continue;

            foreach (var userLanguage in UserLanguages)
            {
                var tag = $"lang-copy {srcCode}->{tgtCode} [user language {(userLanguage.Length == 0 ? "site default" : userLanguage)}]";
                using var _ = UseRequestLanguage(userLanguage);

                // Titles exactly as the explorer would show them to this user.
                List<RestList> srcLists, tgtLists;
                try
                {
                    srcLists = await RestListsAsync(src);
                    tgtLists = await RestListsAsync(tgt);
                }
                catch (Exception ex) { Program.Check($"{tag}: sites reachable", false, ex.Message); continue; }

                var srcDocs = await DefaultLibraryAsync(src, srcLists);
                var tgtDocs = await DefaultLibraryAsync(tgt, tgtLists);
                var srcLib = srcLists.First(l => l.RootUrl.EndsWith("/" + LibUrl, StringComparison.OrdinalIgnoreCase));
                var srcList = srcLists.First(l => l.RootUrl.EndsWith("/" + ListUrl, StringComparison.OrdinalIgnoreCase));

                // A. Files dropped from the built-in library into the target's built-in library:
                //    content-only, parallel transfers, titles and root folder paths as the explorer has them.
                await RunAsync($"{tag} A built-in library '{srcDocs.Title}' -> '{tgtDocs.Title}'", async () =>
                {
                    var res = await CopyEngine.CopyListAsync(src, tgt, srcDocs.Title, new CopyOptions
                    {
                        TargetListTitle = tgtDocs.Title, MergeSchema = false, CopyViews = false, CopyListSettings = false,
                        ParallelFileTransfers = 4,
                        SourceListServerRelativeUrl = srcDocs.RootUrl, TargetListServerRelativeUrl = tgtDocs.RootUrl,
                        TargetSubfolderRelative = "by-path",
                    });
                    return await VerifyLibraryAsync(src, srcDocs.Id, tgt, tgtDocs.Id, res, "by-path/");
                });

                // A2. The same copy with titles only, as a run saved by an older version replays it.
                await RunAsync($"{tag} A2 same, titles only", async () =>
                {
                    var res = await CopyEngine.CopyListAsync(src, tgt, srcDocs.Title, new CopyOptions
                    {
                        TargetListTitle = tgtDocs.Title, MergeSchema = false, CopyViews = false, CopyListSettings = false,
                        ParallelFileTransfers = 4, TargetSubfolderRelative = "by-title",
                    });
                    return await VerifyLibraryAsync(src, srcDocs.Id, tgt, tgtDocs.Id, res, "by-title/");
                });

                // B. Whole custom library recreated on the target (structure + content).
                await RunAsync($"{tag} B custom library '{srcLib.Title}'", async () =>
                {
                    var res = await CopyEngine.CopyListAsync(src, tgt, srcLib.Title, new CopyOptions
                    {
                        TargetListTitle = srcLib.Title, TargetListUrl = LibUrl + "-" + srcCode, ParallelFileTransfers = 4,
                        SourceListServerRelativeUrl = srcLib.RootUrl,
                    });
                    var id = await ListIdByUrlAsync(tgt, LibUrl + "-" + srcCode);
                    return id is null ? "target library was not created" : await VerifyLibraryAsync(src, srcLib.Id, tgt, id.Value, res);
                });

                // C. Whole custom list with folders (structure + content).
                await RunAsync($"{tag} C custom list '{srcList.Title}'", async () =>
                {
                    var res = await CopyEngine.CopyListAsync(src, tgt, srcList.Title, new CopyOptions
                    {
                        TargetListTitle = srcList.Title, TargetListUrl = ListUrl + "-" + srcCode,
                        SourceListServerRelativeUrl = srcList.RootUrl,
                    });
                    var id = await ListIdByUrlAsync(tgt, ListUrl + "-" + srcCode);
                    if (id is null) return "target list was not created";
                    var failed = Failures(res);
                    if (failed != null) return failed;
                    var s = await TitlesAsync(src, srcList.Id);
                    var t = await TitlesAsync(tgt, id.Value);
                    var lost = s.Except(t).ToList();
                    return lost.Count == 0 ? null : $"{lost.Count} of {s.Count} items/folders missing, e.g. '{lost[0]}'";
                });

                // D. Site Pages, whose title is localized too ("Websiteseiten", "Pages du site", ...).
                var srcPages = srcLists.FirstOrDefault(l => l.Template == 119);
                var tgtPages = tgtLists.FirstOrDefault(l => l.Template == 119);
                if (srcPages != null && tgtPages != null)
                    await RunAsync($"{tag} D site pages '{srcPages.Title}' -> '{tgtPages.Title}'", async () =>
                    {
                        var res = await CopyEngine.CopyListAsync(src, tgt, srcPages.Title, new CopyOptions
                        {
                            TargetListTitle = tgtPages.Title, MergeSchema = false, CopyViews = false, CopyListSettings = false,
                            SourceListServerRelativeUrl = srcPages.RootUrl, TargetListServerRelativeUrl = tgtPages.RootUrl,
                        });
                        return Failures(res);
                    });
            }
        }
    }

    private static async Task RunAsync(string name, Func<Task<string?>> body)
    {
        string? problem;
        try { problem = await body(); }
        catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }
        Program.Check(name, problem is null, problem);
    }

    private static string? Failures(CopyResult res)
    {
        var failed = res.Records.Where(r => r.Status == ItemCopyStatus.Failed).ToList();
        return failed.Count == 0 ? null : $"{failed.Count} failed record(s), first: [{failed[0].ItemType}] {failed[0].Message}";
    }

    private static async Task<RestList> DefaultLibraryAsync(SpConnection conn, List<RestList> lists)
    {
        using var ctx = conn.CreateContext();
        var docs = ctx.Web.DefaultDocumentLibrary();
        ctx.Load(docs, l => l.Id);
        await ctx.ExecuteWithRetryAsync();
        return lists.First(l => l.Id == docs.Id);
    }

    private static async Task<Guid?> ListIdByUrlAsync(SpConnection conn, string urlTail)
    {
        var lists = await RestListsAsync(conn);
        return lists.FirstOrDefault(l => l.RootUrl.EndsWith("/" + urlTail, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>Every source file and folder path must exist on the target, and no record may have failed.</summary>
    private static async Task<string?> VerifyLibraryAsync(SpConnection src, Guid srcId, SpConnection tgt, Guid tgtId, CopyResult res,
        string targetPrefix = "")
    {
        var failed = Failures(res);
        // Earlier runs of this test left "by-path"/"by-title" folders in libraries that are also sources.
        var s = (await PathsAsync(src, srcId)).Where(x => !x[2..].StartsWith("by-")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var t = (await PathsAsync(tgt, tgtId)).Where(x => x[2..].StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => x[..2] + x[(2 + targetPrefix.Length)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (s.Count == 0) return "source library is empty, nothing was verified";
        var lost = s.Except(t, StringComparer.OrdinalIgnoreCase).ToList();
        if (lost.Count > 0)
        {
            var files = lost.Count(x => x.StartsWith("F:"));
            return $"{files} file(s) and {lost.Count - files} folder(s) of {s.Count} missing on target, e.g. '{lost[0]}'" +
                (failed is null ? "" : " | " + failed);
        }
        return failed;
    }

    private static async Task<HashSet<string>> PathsAsync(SpConnection conn, Guid listId)
    {
        using var ctx = conn.CreateContext();
        var list = ctx.Web.Lists.GetById(listId);
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteWithRetryAsync();
        var root = list.RootFolder.ServerRelativeUrl;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var query = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>500</RowLimit></View>" };
        do
        {
            var page = list.GetItems(query);
            ctx.Load(page, pg => pg.ListItemCollectionPosition, pg => pg.Include(i => i.FileSystemObjectType, i => i["FileRef"]));
            await ctx.ExecuteWithRetryAsync();
            foreach (var i in page)
            {
                var rel = ((string)i["FileRef"])[(root.Length + 1)..];
                if (rel.StartsWith("Forms", StringComparison.OrdinalIgnoreCase)) continue;
                set.Add((i.FileSystemObjectType == FileSystemObjectType.Folder ? "D:" : "F:") + rel);
            }
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);
        return set;
    }

    private static async Task<HashSet<string>> TitlesAsync(SpConnection conn, Guid listId)
    {
        using var ctx = conn.CreateContext();
        var list = ctx.Web.Lists.GetById(listId);
        var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>2000</RowLimit></View>" });
        ctx.Load(items, its => its.Include(i => i["Title"], i => i["LangStatus"], i => i["LangCustomer"]));
        await ctx.ExecuteWithRetryAsync();
        return items.AsEnumerable().Select(i => $"{i["Title"]}|{i["LangStatus"]}|{i["LangCustomer"]}").ToHashSet();
    }
}
