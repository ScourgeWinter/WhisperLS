using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace FasterWhisperGui.Services;

internal static class LocalizationService
{
    private const string DefaultLanguage = "it";
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperLS",
        "preferences.json");

    private static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["it"] = "Italiano",
            ["en"] = "English",
            ["fr"] = "Français",
            ["es"] = "Español",
            ["de"] = "Deutsch",
            ["pt"] = "Português"
        };

    private static readonly IReadOnlyDictionary<string, string[]> Strings = BuildStrings();
    private static readonly IReadOnlyDictionary<string, string> PortugueseStrings = BuildPortugueseStrings();
    private static readonly string[] LanguageOrder = ["it", "en", "fr", "es", "de"];

    static LocalizationService()
    {
        var saved = LoadSavedLanguage();
        HasSavedLanguage = saved is not null;
        CurrentLanguage = saved ?? DefaultLanguage;
    }

    public static bool HasSavedLanguage { get; private set; }

    public static string CurrentLanguage { get; private set; }

    public static IReadOnlyDictionary<string, string> SupportedLanguages => LanguageNames;

    public static void SetLanguage(string languageCode, bool persist = true)
    {
        if (!LanguageNames.ContainsKey(languageCode))
        {
            languageCode = DefaultLanguage;
        }

        CurrentLanguage = languageCode.ToLowerInvariant();
        if (!persist)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        var payload = JsonSerializer.Serialize(new Preferences(CurrentLanguage));
        File.WriteAllText(PreferencesPath, payload);
        HasSavedLanguage = true;
    }

    public static string Get(string italianSource)
    {
        if (!Strings.TryGetValue(italianSource, out var translations))
        {
            return italianSource;
        }

        return GetTranslation(italianSource, translations);
    }

    public static string Format(string italianSource, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(italianSource), args);

    public static string TranslateDynamic(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || CurrentLanguage == DefaultLanguage)
        {
            return value;
        }

        if (Strings.TryGetValue(value, out var exact))
        {
            return GetTranslation(value, exact);
        }

        var result = value;
        foreach (var pair in Strings.OrderByDescending(item => item.Key.Length))
        {
            if (pair.Key.Length < 4 || !result.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result = result.Replace(
                pair.Key,
                GetTranslation(pair.Key, pair.Value),
                StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public static void ApplyToVisualTree(DependencyObject root)
    {
        ApplyToElement(root);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            ApplyToVisualTree(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static void ApplyToElement(DependencyObject element)
    {
        if (element is TextBlock textBlock &&
            textBlock.GetBindingExpression(TextBlock.TextProperty) is null &&
            !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            var translated = TranslateFromAnyLanguage(textBlock.Text);
            if (!translated.Equals(textBlock.Text, StringComparison.Ordinal))
            {
                textBlock.Text = translated;
            }
        }
        if (element is ContentControl contentControl &&
            contentControl.GetBindingExpression(ContentControl.ContentProperty) is null &&
            contentControl.Content is string content)
        {
            var translated = TranslateFromAnyLanguage(content);
            if (!translated.Equals(content, StringComparison.Ordinal))
            {
                contentControl.Content = translated;
            }
        }
        if (element is ComboBox comboBox && comboBox.Header is string comboHeader)
        {
            var translated = TranslateFromAnyLanguage(comboHeader);
            if (!translated.Equals(comboHeader, StringComparison.Ordinal))
            {
                comboBox.Header = translated;
            }
        }
        if (element is ComboBox itemComboBox)
        {
            foreach (var item in itemComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Content is not string itemText)
                {
                    continue;
                }

                var translated = TranslateFromAnyLanguage(itemText);
                if (!translated.Equals(itemText, StringComparison.Ordinal))
                {
                    item.Content = translated;
                }
            }
        }
        if (element is NumberBox numberBox && numberBox.Header is string numberHeader)
        {
            var translated = TranslateFromAnyLanguage(numberHeader);
            if (!translated.Equals(numberHeader, StringComparison.Ordinal))
            {
                numberBox.Header = translated;
            }
        }
        if (element is Expander expander && expander.Header is string expanderHeader)
        {
            var translated = TranslateFromAnyLanguage(expanderHeader);
            if (!translated.Equals(expanderHeader, StringComparison.Ordinal))
            {
                expander.Header = translated;
            }
        }
        if (element is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.PlaceholderText))
        {
            var translated = TranslateFromAnyLanguage(textBox.PlaceholderText);
            if (!translated.Equals(textBox.PlaceholderText, StringComparison.Ordinal))
            {
                textBox.PlaceholderText = translated;
            }
        }
        if (element is AppBarButton appBarButton && !string.IsNullOrWhiteSpace(appBarButton.Label))
        {
            var translated = TranslateFromAnyLanguage(appBarButton.Label);
            if (!translated.Equals(appBarButton.Label, StringComparison.Ordinal))
            {
                appBarButton.Label = translated;
            }
        }
        if (element is ToggleSwitch toggleSwitch)
        {
            if (toggleSwitch.OnContent is string onContent)
            {
                var translated = TranslateFromAnyLanguage(onContent);
                if (!translated.Equals(onContent, StringComparison.Ordinal))
                {
                    toggleSwitch.OnContent = translated;
                }
            }
            if (toggleSwitch.OffContent is string offContent)
            {
                var translated = TranslateFromAnyLanguage(offContent);
                if (!translated.Equals(offContent, StringComparison.Ordinal))
                {
                    toggleSwitch.OffContent = translated;
                }
            }
        }

        var automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(automationName))
        {
            var translated = TranslateFromAnyLanguage(automationName);
            if (!translated.Equals(automationName, StringComparison.Ordinal))
            {
                AutomationProperties.SetName(element, translated);
            }
        }
        var helpText = AutomationProperties.GetHelpText(element);
        if (!string.IsNullOrWhiteSpace(helpText))
        {
            var translated = TranslateFromAnyLanguage(helpText);
            if (!translated.Equals(helpText, StringComparison.Ordinal))
            {
                AutomationProperties.SetHelpText(element, translated);
            }
        }
        var toolTip = ToolTipService.GetToolTip(element);
        if (toolTip is string toolTipText)
        {
            var translated = TranslateFromAnyLanguage(toolTipText);
            if (!translated.Equals(toolTipText, StringComparison.Ordinal))
            {
                ToolTipService.SetToolTip(element, translated);
            }
        }
    }

    private static string TranslateFromAnyLanguage(string value)
    {
        foreach (var pair in Strings)
        {
            if (pair.Key.Equals(value, StringComparison.Ordinal) ||
                pair.Value.Any(candidate => candidate.Equals(value, StringComparison.Ordinal)) ||
                PortugueseStrings.TryGetValue(pair.Key, out var portuguese) &&
                portuguese.Equals(value, StringComparison.Ordinal))
            {
                return GetTranslation(pair.Key, pair.Value);
            }
        }
        return TranslateDynamic(value);
    }

    private static string GetTranslation(string italianSource, string[] translations)
    {
        if (CurrentLanguage == "pt")
        {
            return PortugueseStrings.TryGetValue(italianSource, out var portuguese)
                ? portuguese
                : translations[1];
        }

        var index = Array.IndexOf(LanguageOrder, CurrentLanguage);
        return translations[index >= 0 ? index : 0];
    }

    private static string? LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return null;
            }
            var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath));
            return preferences is not null && LanguageNames.ContainsKey(preferences.InterfaceLanguage)
                ? preferences.InterfaceLanguage.ToLowerInvariant()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string[]> BuildStrings() =>
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // Italian, English, French, Spanish, German.
            ["WhisperLS - Made with love ❤️"] = ["WhisperLS - Made with love ❤️", "WhisperLS - Made with love ❤️", "WhisperLS - Créé avec amour ❤️", "WhisperLS - Hecho con amor ❤️", "WhisperLS - Mit Liebe gemacht ❤️"],
            ["Sistema offline integrato di trascrizione audio-to-text."] = ["Sistema offline integrato di trascrizione audio-to-text.", "Integrated offline audio-to-text transcription system.", "Système intégré hors ligne de transcription audio en texte.", "Sistema integrado sin conexión de transcripción de audio a texto.", "Integriertes Offline-System zur Audio-zu-Text-Transkription."],
            ["FILE SORGENTE"] = ["FILE SORGENTE", "SOURCE FILES", "FICHIERS SOURCE", "ARCHIVOS DE ORIGEN", "QUELLDATEIEN"],
            ["Nessun file selezionato"] = ["Nessun file selezionato", "No file selected", "Aucun fichier sélectionné", "Ningún archivo seleccionado", "Keine Datei ausgewählt"],
            ["Scegli tracce"] = ["Scegli tracce", "Choose tracks", "Choisir les pistes", "Elegir pistas", "Spuren auswählen"],
            ["Seleziona una o più tracce audio o video"] = ["Seleziona una o più tracce audio o video", "Select one or more audio or video tracks", "Sélectionner une ou plusieurs pistes audio ou vidéo", "Selecciona una o varias pistas de audio o vídeo", "Eine oder mehrere Audio- oder Videospuren auswählen"],
            ["Gestisci coda"] = ["Gestisci coda", "Manage queue", "Gérer la file", "Gestionar cola", "Warteschlange verwalten"],
            ["Apri il manager della coda"] = ["Apri il manager della coda", "Open queue manager", "Ouvrir le gestionnaire de file", "Abrir el gestor de cola", "Warteschlangenmanager öffnen"],
            ["Impostazioni"] = ["Impostazioni", "Settings", "Paramètres", "Ajustes", "Einstellungen"],
            ["Configura la trascrizione selezionata."] = ["Configura la trascrizione selezionata.", "Configure the selected transcription.", "Configurez la transcription sélectionnée.", "Configura la transcripción seleccionada.", "Konfiguriere die ausgewählte Transkription."],
            ["Modifica"] = ["Modifica", "Edit", "Modifier", "Editar", "Bearbeiten"],
            ["Modifica impostazioni"] = ["Modifica impostazioni", "Edit settings", "Modifier les paramètres", "Editar ajustes", "Einstellungen bearbeiten"],
            ["Modello"] = ["Modello", "Model", "Modèle", "Modelo", "Modell"],
            ["Lingua"] = ["Lingua", "Transcription language", "Langue de transcription", "Idioma de transcripción", "Transkriptionssprache"],
            ["Lingua dell'interfaccia"] = ["Lingua dell'interfaccia", "Interface language", "Langue de l’interface", "Idioma de la interfaz", "Sprache der Benutzeroberfläche"],
            ["Dispositivo"] = ["Dispositivo", "Device", "Appareil", "Dispositivo", "Gerät"],
            ["Automatico"] = ["Automatico", "Automatic", "Automatique", "Automático", "Automatisch"],
            ["Rilevamento automatico"] = ["Rilevamento automatico", "Automatic detection", "Détection automatique", "Detección automática", "Automatische Erkennung"],
            ["Filtro voce (VAD)"] = ["Filtro voce (VAD)", "Voice filter (VAD)", "Filtre vocal (VAD)", "Filtro de voz (VAD)", "Sprachfilter (VAD)"],
            ["Timestamp per parola"] = ["Timestamp per parola", "Word timestamps", "Horodatage par mot", "Marcas de tiempo por palabra", "Wortzeitstempel"],
            ["Attivato"] = ["Attivato", "On", "Activé", "Activado", "Aktiviert"],
            ["Disattivato"] = ["Disattivato", "Off", "Désactivé", "Desactivado", "Deaktiviert"],
            ["Solo intervallo"] = ["Solo intervallo", "Selected range only", "Plage sélectionnée uniquement", "Solo intervalo seleccionado", "Nur ausgewählter Bereich"],
            ["Inizio intervallo"] = ["Inizio intervallo", "Range start", "Début de la plage", "Inicio del intervalo", "Bereichsanfang"],
            ["Fine intervallo"] = ["Fine intervallo", "Range end", "Fin de la plage", "Fin del intervalo", "Bereichsende"],
            ["Cartella di output"] = ["Cartella di output", "Output folder", "Dossier de sortie", "Carpeta de salida", "Ausgabeordner"],
            ["Stessa cartella del file sorgente"] = ["Stessa cartella del file sorgente", "Same folder as the source file", "Même dossier que le fichier source", "Misma carpeta que el archivo de origen", "Gleicher Ordner wie die Quelldatei"],
            ["Cambia"] = ["Cambia", "Change", "Modifier", "Cambiar", "Ändern"],
            ["Fatto"] = ["Fatto", "Done", "Terminé", "Hecho", "Fertig"],
            ["Analisi in corso..."] = ["Analisi in corso...", "Analyzing...", "Analyse en cours…", "Analizando…", "Analyse läuft…"],
            ["Analisi del file e dell'hardware..."] = ["Analisi del file e dell'hardware...", "Analyzing files and hardware...", "Analyse des fichiers et du matériel…", "Analizando archivos y hardware…", "Dateien und Hardware werden analysiert…"],
            ["Durata sorgente"] = ["Durata sorgente", "Source duration", "Durée de la source", "Duración de origen", "Quelldauer"],
            ["Tempo indicativo"] = ["Tempo indicativo", "Estimated time", "Temps estimé", "Tiempo estimado", "Geschätzte Zeit"],
            ["Non disponibile"] = ["Non disponibile", "Not available", "Non disponible", "No disponible", "Nicht verfügbar"],
            ["Stato"] = ["Stato", "Status", "État", "Estado", "Status"],
            ["Avvia trascrizione"] = ["Avvia trascrizione", "Start transcription", "Démarrer la transcription", "Iniciar transcripción", "Transkription starten"],
            ["Trascrizione in corso..."] = ["Trascrizione in corso...", "Transcription in progress...", "Transcription en cours…", "Transcripción en curso…", "Transkription läuft…"],
            ["Riprendi"] = ["Riprendi", "Resume", "Reprendre", "Reanudar", "Fortsetzen"],
            ["Annulla"] = ["Annulla", "Cancel", "Annuler", "Cancelar", "Abbrechen"],
            ["Ripristina coda"] = ["Ripristina coda", "Restore queue", "Restaurer la file", "Restaurar cola", "Warteschlange wiederherstellen"],
            ["Apri cartella output"] = ["Apri cartella output", "Open output folder", "Ouvrir le dossier de sortie", "Abrir carpeta de salida", "Ausgabeordner öffnen"],
            ["Console"] = ["Console", "Console", "Console", "Consola", "Konsole"],
            ["Mostra"] = ["Mostra", "Show", "Afficher", "Mostrar", "Anzeigen"],
            ["Nascondi"] = ["Nascondi", "Hide", "Masquer", "Ocultar", "Ausblenden"],
            ["Mostra console"] = ["Mostra console", "Show console", "Afficher la console", "Mostrar consola", "Konsole anzeigen"],
            ["Nascondi console"] = ["Nascondi console", "Hide console", "Masquer la console", "Ocultar consola", "Konsole ausblenden"],
            ["In attesa di attività..."] = ["In attesa di attività...", "Waiting for activity...", "En attente d’activité…", "Esperando actividad…", "Warten auf Aktivität…"],
            ["Manager della coda"] = ["Manager della coda", "Queue manager", "Gestionnaire de file", "Gestor de cola", "Warteschlangenmanager"],
            ["Combina liberamente lavori singoli e trascrizioni multiparte."] = ["Combina liberamente lavori singoli e trascrizioni multiparte.", "Freely combine individual jobs and multipart transcriptions.", "Combinez librement les tâches individuelles et les transcriptions multiparts.", "Combina libremente trabajos individuales y transcripciones multiparte.", "Einzelaufträge und mehrteilige Transkriptionen frei kombinieren."],
            ["Output"] = ["Output", "Output", "Sortie", "Salida", "Ausgabe"],
            ["Su"] = ["Su", "Up", "Monter", "Subir", "Nach oben"],
            ["Giù"] = ["Giù", "Down", "Descendre", "Bajar", "Nach unten"],
            ["Unisci al precedente"] = ["Unisci al precedente", "Merge with previous", "Fusionner avec la précédente", "Unir con el anterior", "Mit vorherigem zusammenführen"],
            ["Separa"] = ["Separa", "Split", "Séparer", "Separar", "Trennen"],
            ["Rimuovi"] = ["Rimuovi", "Remove", "Supprimer", "Eliminar", "Entfernen"],
            ["Applica"] = ["Applica", "Apply", "Appliquer", "Aplicar", "Anwenden"],
            ["SINGOLO"] = ["SINGOLO", "SINGLE", "INDIVIDUEL", "INDIVIDUAL", "EINZELN"],
            ["MULTIPARTE"] = ["MULTIPARTE", "MULTIPART", "MULTIPART", "MULTIPARTE", "MEHRTEILIG"],
            ["LAVORO"] = ["LAVORO", "JOB", "TÂCHE", "TRABAJO", "AUFTRAG"],
            ["Model Manager"] = ["Model Manager", "Model Manager", "Gestionnaire de modèles", "Gestor de modelos", "Modellverwaltung"],
            ["Disponibilità locale e appropriatezza per questo computer."] = ["Disponibilità locale e appropriatezza per questo computer.", "Local availability and suitability for this computer.", "Disponibilité locale et adéquation à cet ordinateur.", "Disponibilidad local e idoneidad para este equipo.", "Lokale Verfügbarkeit und Eignung für diesen Computer."],
            ["Rilevamento hardware..."] = ["Rilevamento hardware...", "Detecting hardware...", "Détection du matériel…", "Detectando hardware…", "Hardware wird erkannt…"],
            ["Controllo dei modelli locali..."] = ["Controllo dei modelli locali...", "Checking local models...", "Vérification des modèles locaux…", "Comprobando modelos locales…", "Lokale Modelle werden geprüft…"],
            ["Appropriatezza"] = ["Appropriatezza", "Suitability", "Adéquation", "Idoneidad", "Eignung"],
            ["PRESENTE"] = ["PRESENTE", "INSTALLED", "INSTALLÉ", "INSTALADO", "INSTALLIERT"],
            ["NON PRESENTE"] = ["NON PRESENTE", "NOT INSTALLED", "NON INSTALLÉ", "NO INSTALADO", "NICHT INSTALLIERT"],
            ["Pronto"] = ["Pronto", "Ready", "Prêt", "Listo", "Bereit"],
            ["Scarica"] = ["Scarica", "Download", "Télécharger", "Descargar", "Herunterladen"],
            ["Disinstalla"] = ["Disinstalla", "Uninstall", "Désinstaller", "Desinstalar", "Deinstallieren"],
            ["Aggiorna"] = ["Aggiorna", "Refresh", "Actualiser", "Actualizar", "Aktualisieren"],
            ["Gestisci modelli"] = ["Gestisci modelli", "Manage models", "Gérer les modèles", "Gestionar modelos", "Modelle verwalten"],
            ["Controlla aggiornamenti"] = ["Controlla aggiornamenti", "Check for updates", "Rechercher des mises à jour", "Buscar actualizaciones", "Nach Updates suchen"],
            ["Riduci a icona"] = ["Riduci a icona", "Minimize", "Réduire", "Minimizar", "Minimieren"],
            ["Massimizza"] = ["Massimizza", "Maximize", "Agrandir", "Maximizar", "Maximieren"],
            ["Ripristina"] = ["Ripristina", "Restore", "Restaurer", "Restaurar", "Wiederherstellen"],
            ["Chiudi"] = ["Chiudi", "Close", "Fermer", "Cerrar", "Schließen"],
            ["Chiudi impostazioni"] = ["Chiudi impostazioni", "Close settings", "Fermer les paramètres", "Cerrar ajustes", "Einstellungen schließen"],
            ["Chiudi manager della coda"] = ["Chiudi manager della coda", "Close queue manager", "Fermer le gestionnaire de file", "Cerrar el gestor de cola", "Warteschlangenmanager schließen"],
            ["Chiudi Model Manager"] = ["Chiudi Model Manager", "Close Model Manager", "Fermer le gestionnaire de modèles", "Cerrar el gestor de modelos", "Modellverwaltung schließen"],
            ["Avanzamento trascrizione"] = ["Avanzamento trascrizione", "Transcription progress", "Progression de la transcription", "Progreso de transcripción", "Transkriptionsfortschritt"],
            ["Cronologia della console"] = ["Cronologia della console", "Console history", "Historique de la console", "Historial de consola", "Konsolenverlauf"],
            ["Informazioni sul dispositivo"] = ["Informazioni sul dispositivo", "Device information", "Informations sur l’appareil", "Información del dispositivo", "Geräteinformationen"],
            ["Informazioni sul beam size"] = ["Informazioni sul beam size", "Beam size information", "Informations sur le beam size", "Información sobre beam size", "Informationen zur Beam Size"],
            ["Informazioni sul filtro voce"] = ["Informazioni sul filtro voce", "Voice filter information", "Informations sur le filtre vocal", "Información del filtro de voz", "Informationen zum Sprachfilter"],
            ["Informazioni sui timestamp per parola"] = ["Informazioni sui timestamp per parola", "Word timestamp information", "Informations sur l’horodatage par mot", "Información sobre marcas de tiempo por palabra", "Informationen zu Wortzeitstempeln"],
            ["Nome output del lavoro"] = ["Nome output del lavoro", "Job output name", "Nom de sortie de la tâche", "Nombre de salida del trabajo", "Ausgabename des Auftrags"],
            ["Rimuovi lavoro dalla coda"] = ["Rimuovi lavoro dalla coda", "Remove job from queue", "Supprimer la tâche de la file", "Eliminar trabajo de la cola", "Auftrag aus der Warteschlange entfernen"],
            ["Trascina verticalmente per riordinare"] = ["Trascina verticalmente per riordinare", "Drag vertically to reorder", "Faites glisser verticalement pour réorganiser", "Arrastra verticalmente para reordenar", "Zum Sortieren vertikal ziehen"],
            ["Riprende la trascrizione dal checkpoint rimasto dopo un'interruzione"] = ["Riprende la trascrizione dal checkpoint rimasto dopo un'interruzione", "Resume transcription from the checkpoint left after an interruption", "Reprendre la transcription depuis le point de contrôle conservé après une interruption", "Reanuda la transcripción desde el punto de control guardado tras una interrupción", "Transkription vom nach einer Unterbrechung gespeicherten Prüfpunkt fortsetzen"],
            ["Ricarica la coda rimasta dopo un'interruzione"] = ["Ricarica la coda rimasta dopo un'interruzione", "Reload the queue left after an interruption", "Recharger la file conservée après une interruption", "Recarga la cola guardada tras una interrupción", "Nach einer Unterbrechung gespeicherte Warteschlange neu laden"],
            ["Genera un documento di testo semplice"] = ["Genera un documento di testo semplice", "Create a plain text document", "Créer un document texte simple", "Genera un documento de texto sencillo", "Ein einfaches Textdokument erstellen"],
            ["Genera sottotitoli con indicatori temporali"] = ["Genera sottotitoli con indicatori temporali", "Create subtitles with timestamps", "Créer des sous-titres avec horodatage", "Genera subtítulos con marcas de tiempo", "Untertitel mit Zeitstempeln erstellen"],
            ["Genera un documento Microsoft Word"] = ["Genera un documento Microsoft Word", "Create a Microsoft Word document", "Créer un document Microsoft Word", "Genera un documento Microsoft Word", "Ein Microsoft-Word-Dokument erstellen"],
            ["Trascrive soltanto la porzione compresa tra l'inizio e la fine indicate"] = ["Trascrive soltanto la porzione compresa tra l'inizio e la fine indicate", "Transcribe only the portion between the specified start and end", "Transcrire uniquement la partie comprise entre le début et la fin indiqués", "Transcribe solo la parte comprendida entre el inicio y el fin indicados", "Nur den Abschnitt zwischen dem angegebenen Anfang und Ende transkribieren"],
            ["La percentuale stima l'equilibrio fra velocità e risorse richieste."] = ["La percentuale stima l'equilibrio fra velocità e risorse richieste.", "The percentage estimates the balance between speed and required resources.", "Le pourcentage estime l’équilibre entre vitesse et ressources requises.", "El porcentaje estima el equilibrio entre velocidad y recursos necesarios.", "Der Prozentsatz schätzt das Verhältnis zwischen Geschwindigkeit und Ressourcenbedarf."],
            ["Stima indicativa: non è un benchmark."] = ["Stima indicativa: non è un benchmark.", "Indicative estimate: this is not a benchmark.", "Estimation indicative : il ne s’agit pas d’un benchmark.", "Estimación orientativa: no es una prueba de rendimiento.", "Unverbindliche Schätzung: kein Benchmark."],
            ["Italiano"] = ["Italiano", "Italian", "Italien", "Italiano", "Italienisch"],
            ["English"] = ["Inglese", "English", "Anglais", "Inglés", "Englisch"],
            ["Français"] = ["Francese", "French", "Français", "Francés", "Französisch"],
            ["Español"] = ["Spagnolo", "Spanish", "Espagnol", "Español", "Spanisch"],
            ["Deutsch"] = ["Tedesco", "German", "Allemand", "Alemán", "Deutsch"],
            ["Português"] = ["Portoghese", "Portuguese", "Portugais", "Portugués", "Portugiesisch"],
            ["Seleziona la lingua"] = ["Seleziona la lingua", "Choose your language", "Choisissez votre langue", "Elige tu idioma", "Sprache auswählen"],
            ["Potrai modificarla in qualsiasi momento dalle impostazioni."] = ["Potrai modificarla in qualsiasi momento dalle impostazioni.", "You can change it at any time in Settings.", "Vous pourrez la modifier à tout moment dans les paramètres.", "Podrás cambiarlo en cualquier momento desde los ajustes.", "Du kannst sie jederzeit in den Einstellungen ändern."],
            ["Continua"] = ["Continua", "Continue", "Continuer", "Continuar", "Weiter"],
            ["Aggiornamento disponibile"] = ["Aggiornamento disponibile", "Update available", "Mise à jour disponible", "Actualización disponible", "Update verfügbar"],
            ["Novità della versione"] = ["Novità della versione", "What's new", "Nouveautés", "Novedades", "Neuigkeiten"],
            ["Annullato"] = ["Annullato", "Cancelled", "Annulé", "Cancelado", "Abgebrochen"],
            ["Errore"] = ["Errore", "Error", "Erreur", "Error", "Fehler"],
            ["Avviso"] = ["Avviso", "Warning", "Avertissement", "Aviso", "Warnung"],
            ["Successo"] = ["Successo", "Success", "Succès", "Éxito", "Erfolg"],
            ["Parte"] = ["Parte", "Part", "Partie", "Parte", "Teil"],
            ["Lavoro"] = ["Lavoro", "Job", "Tâche", "Trabajo", "Auftrag"],
            ["lavoro"] = ["lavoro", "job", "tâche", "trabajo", "Auftrag"],
            ["lavori"] = ["lavori", "jobs", "tâches", "trabajos", "Aufträge"],
            ["traccia"] = ["traccia", "track", "piste", "pista", "Spur"],
            ["tracce"] = ["tracce", "tracks", "pistes", "pistas", "Spuren"],
            ["parti"] = ["parti", "parts", "parties", "partes", "Teile"],
            ["attivo"] = ["attivo", "on", "actif", "activo", "aktiv"],
            ["disattivo"] = ["disattivo", "off", "inactif", "inactivo", "inaktiv"],
            ["attivi"] = ["attivi", "on", "actifs", "activos", "aktiv"],
            ["disattivati"] = ["disattivati", "off", "désactivés", "desactivados", "deaktiviert"],
            ["Intervallo"] = ["Intervallo", "Range", "Plage", "Intervalo", "Bereich"],
            ["cartella del file sorgente"] = ["cartella del file sorgente", "source file folder", "dossier du fichier source", "carpeta del archivo de origen", "Ordner der Quelldatei"],
            ["Gestisci coda · {0} · {1}"] = ["Gestisci coda · {0} · {1}", "Manage queue · {0} · {1}", "Gérer la file · {0} · {1}", "Gestionar cola · {0} · {1}", "Warteschlange · {0} · {1}"],
            ["{0} · {1}"] = ["{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}"],
            ["Avvia coda ({0})"] = ["Avvia coda ({0})", "Start queue ({0})", "Démarrer la file ({0})", "Iniciar cola ({0})", "Warteschlange starten ({0})"],
            ["Eccellente"] = ["Eccellente", "Excellent", "Excellent", "Excelente", "Ausgezeichnet"],
            ["Adatto"] = ["Adatto", "Suitable", "Adapté", "Adecuado", "Geeignet"],
            ["Discreto"] = ["Discreto", "Fair", "Correct", "Aceptable", "Passabel"],
            ["Impegnativo"] = ["Impegnativo", "Demanding", "Exigeant", "Exigente", "Anspruchsvoll"],
            ["Poco adatto"] = ["Poco adatto", "Poor fit", "Peu adapté", "Poco adecuado", "Wenig geeignet"],
            ["Coda"] = ["Coda", "Queue", "File", "Cola", "Warteschlange"],
            ["Modello pronto. Puoi avviare la trascrizione."] = ["Modello pronto. Puoi avviare la trascrizione.", "Model ready. You can start transcription.", "Modèle prêt. Vous pouvez démarrer la transcription.", "Modelo listo. Puedes iniciar la transcripción.", "Modell bereit. Du kannst die Transkription starten."],
            ["Seleziona almeno un formato di output."] = ["Seleziona almeno un formato di output.", "Select at least one output format.", "Sélectionnez au moins un format de sortie.", "Selecciona al menos un formato de salida.", "Wähle mindestens ein Ausgabeformat."],
            ["Seleziona un file e un modello."] = ["Seleziona un file e un modello.", "Select a file and a model.", "Sélectionnez un fichier et un modèle.", "Selecciona un archivo y un modelo.", "Wähle eine Datei und ein Modell."],
            ["Operazione annullata."] = ["Operazione annullata.", "Operation cancelled.", "Opération annulée.", "Operación cancelada.", "Vorgang abgebrochen."],
            ["La traccia audio selezionata è stata trascritta con successo!"] = ["La traccia audio selezionata è stata trascritta con successo!", "The selected audio track was transcribed successfully!", "La piste audio sélectionnée a été transcrite avec succès !", "¡La pista de audio seleccionada se transcribió correctamente!", "Die ausgewählte Audiospur wurde erfolgreich transkribiert!"],
            ["Trascrizione completata"] = ["Trascrizione completata", "Transcription completed", "Transcription terminée", "Transcripción completada", "Transkription abgeschlossen"],
            ["Trascrizione interrotta"] = ["Trascrizione interrotta", "Transcription interrupted", "Transcription interrompue", "Transcripción interrumpida", "Transkription unterbrochen"],
            ["Scarica modello"] = ["Scarica modello", "Download model", "Télécharger le modèle", "Descargar modelo", "Modell herunterladen"],
            ["Coda in elaborazione..."] = ["Coda in elaborazione...", "Processing queue...", "Traitement de la file…", "Procesando cola…", "Warteschlange wird verarbeitet…"],
            ["Aggiornamento dei modelli locali..."] = ["Aggiornamento dei modelli locali...", "Refreshing local models...", "Actualisation des modèles locaux…", "Actualizando modelos locales…", "Lokale Modelle werden aktualisiert…"],
            ["Controllo del modello locale..."] = ["Controllo del modello locale...", "Checking local model...", "Vérification du modèle local…", "Comprobando modelo local…", "Lokales Modell wird geprüft…"],
            ["Download del modello..."] = ["Download del modello...", "Downloading model...", "Téléchargement du modèle…", "Descargando modelo…", "Modell wird heruntergeladen…"],
            ["Modello scaricato e verificato."] = ["Modello scaricato e verificato.", "Model downloaded and verified.", "Modèle téléchargé et vérifié.", "Modelo descargado y verificado.", "Modell heruntergeladen und überprüft."],
            ["Analisi non completata."] = ["Analisi non completata.", "Analysis not completed.", "Analyse non terminée.", "Análisis no completado.", "Analyse nicht abgeschlossen."],
            ["Impossibile controllare gli aggiornamenti. Verifica la connessione."] = ["Impossibile controllare gli aggiornamenti. Verifica la connessione.", "Unable to check for updates. Check your connection.", "Impossible de rechercher les mises à jour. Vérifiez votre connexion.", "No se pueden buscar actualizaciones. Comprueba la conexión.", "Updates konnten nicht geprüft werden. Verbindung prüfen."],
            ["Non ricordarmelo più per questa versione"] = ["Non ricordarmelo più per questa versione", "Don't remind me again for this version", "Ne plus me le rappeler pour cette version", "No volver a recordarlo para esta versión", "Für diese Version nicht mehr erinnern"],
            ["Consulta la release GitHub per i dettagli."] = ["Consulta la release GitHub per i dettagli.", "See the GitHub release for details.", "Consultez la release GitHub pour plus de détails.", "Consulta la release de GitHub para más detalles.", "Details stehen im GitHub-Release."],
            ["Preparazione del download..."] = ["Preparazione del download...", "Preparing download...", "Préparation du téléchargement…", "Preparando descarga…", "Download wird vorbereitet…"],
            ["Aggiornamento di WhisperLS"] = ["Aggiornamento di WhisperLS", "WhisperLS update", "Mise à jour de WhisperLS", "Actualización de WhisperLS", "WhisperLS-Update"],
            ["L'aggiornamento verrà ricordato alla prossima apertura."] = ["L'aggiornamento verrà ricordato alla prossima apertura.", "The update will be offered again the next time the app opens.", "La mise à jour sera proposée à nouveau au prochain démarrage.", "La actualización se ofrecerá de nuevo en el próximo inicio.", "Das Update wird beim nächsten Start erneut angeboten."],
            ["Download dell'aggiornamento annullato."] = ["Download dell'aggiornamento annullato.", "Update download cancelled.", "Téléchargement de la mise à jour annulé.", "Descarga de la actualización cancelada.", "Update-Download abgebrochen."],
            ["Download completato e verificato."] = ["Download completato e verificato.", "Download completed and verified.", "Téléchargement terminé et vérifié.", "Descarga completada y verificada.", "Download abgeschlossen und überprüft."],
            ["Finestra principale non disponibile."] = ["Finestra principale non disponibile.", "Main window unavailable.", "Fenêtre principale indisponible.", "Ventana principal no disponible.", "Hauptfenster nicht verfügbar."],
            ["Attendi il completamento della disinstallazione."] = ["Attendi il completamento della disinstallazione.", "Wait for the uninstall to complete.", "Attendez la fin de la désinstallation.", "Espera a que termine la desinstalación.", "Warte, bis die Deinstallation abgeschlossen ist."],
            ["Impossibile leggere lo stato dei modelli."] = ["Impossibile leggere lo stato dei modelli.", "Unable to read model status.", "Impossible de lire l’état des modèles.", "No se puede leer el estado de los modelos.", "Modellstatus konnte nicht gelesen werden."],
            ["Il modello deve essere scaricato prima della trascrizione."] = ["Il modello deve essere scaricato prima della trascrizione.", "The model must be downloaded before transcription.", "Le modèle doit être téléchargé avant la transcription.", "El modelo debe descargarse antes de la transcripción.", "Das Modell muss vor der Transkription heruntergeladen werden."],
            ["Modello pronto. La stima è indicativa e non include l'avvio iniziale."] = ["Modello pronto. La stima è indicativa e non include l'avvio iniziale.", "Model ready. The estimate is indicative and excludes initial startup.", "Modèle prêt. L’estimation est indicative et exclut le démarrage initial.", "Modelo listo. La estimación es orientativa y no incluye el arranque inicial.", "Modell bereit. Die Schätzung ist unverbindlich und enthält nicht den Erststart."],
            ["La cartella di output non è disponibile."] = ["La cartella di output non è disponibile.", "The output folder is unavailable.", "Le dossier de sortie est indisponible.", "La carpeta de salida no está disponible.", "Der Ausgabeordner ist nicht verfügbar."],
            ["Il checkpoint non è più disponibile."] = ["Il checkpoint non è più disponibile.", "The checkpoint is no longer available.", "Le point de contrôle n’est plus disponible.", "El punto de control ya no está disponible.", "Der Prüfpunkt ist nicht mehr verfügbar."],
            ["Ripresa della trascrizione..."] = ["Ripresa della trascrizione...", "Resuming transcription...", "Reprise de la transcription…", "Reanudando transcripción…", "Transkription wird fortgesetzt…"],
            ["La trascrizione interrotta è stata ripresa e completata con successo!"] = ["La trascrizione interrotta è stata ripresa e completata con successo!", "The interrupted transcription was resumed and completed successfully!", "La transcription interrompue a repris et s’est terminée avec succès !", "¡La transcripción interrumpida se reanudó y completó correctamente!", "Die unterbrochene Transkription wurde erfolgreich fortgesetzt und abgeschlossen!"],
            ["Ripresa annullata. Il checkpoint è stato conservato."] = ["Ripresa annullata. Il checkpoint è stato conservato.", "Resume cancelled. The checkpoint was preserved.", "Reprise annulée. Le point de contrôle a été conservé.", "Reanudación cancelada. Se conservó el punto de control.", "Fortsetzung abgebrochen. Der Prüfpunkt wurde beibehalten."],
            ["Interrompere la coda?"] = ["Interrompere la coda?", "Stop the queue?", "Interrompre la file ?", "¿Interrumpir la cola?", "Warteschlange anhalten?"],
            ["Puoi saltare soltanto il lavoro corrente oppure annullare tutti i lavori rimanenti."] = ["Puoi saltare soltanto il lavoro corrente oppure annullare tutti i lavori rimanenti.", "You can skip only the current job or cancel all remaining jobs.", "Vous pouvez ignorer uniquement la tâche actuelle ou annuler toutes les tâches restantes.", "Puedes omitir solo el trabajo actual o cancelar todos los restantes.", "Du kannst nur den aktuellen Auftrag überspringen oder alle verbleibenden abbrechen."],
            ["Solo lavoro corrente"] = ["Solo lavoro corrente", "Current job only", "Tâche actuelle uniquement", "Solo trabajo actual", "Nur aktueller Auftrag"],
            ["Intera coda"] = ["Intera coda", "Entire queue", "Toute la file", "Toda la cola", "Gesamte Warteschlange"],
            ["La traccia corrente verrà interrotta; la coda proseguirà."] = ["La traccia corrente verrà interrotta; la coda proseguirà.", "The current track will stop; the queue will continue.", "La piste actuelle sera interrompue ; la file continuera.", "La pista actual se detendrá; la cola continuará.", "Die aktuelle Spur wird beendet; die Warteschlange läuft weiter."],
            ["Coda interrotta ripristinata. Puoi riprendere l'elaborazione."] = ["Coda interrotta ripristinata. Puoi riprendere l'elaborazione.", "Interrupted queue restored. You can resume processing.", "File interrompue restaurée. Vous pouvez reprendre le traitement.", "Cola interrumpida restaurada. Puedes reanudar el procesamiento.", "Unterbrochene Warteschlange wiederhergestellt. Verarbeitung kann fortgesetzt werden."],
            ["Sostituire la trascrizione esistente?"] = ["Sostituire la trascrizione esistente?", "Replace the existing transcription?", "Remplacer la transcription existante ?", "¿Sustituir la transcripción existente?", "Vorhandene Transkription ersetzen?"],
            ["Sostituisci"] = ["Sostituisci", "Replace", "Remplacer", "Sustituir", "Ersetzen"],
            ["Da 00:00:00"] = ["Da 00:00:00", "From 00:00:00", "De 00:00:00", "Desde 00:00:00", "Von 00:00:00"],
            ["A 00:00:00"] = ["A 00:00:00", "To 00:00:00", "À 00:00:00", "Hasta 00:00:00", "Bis 00:00:00"],
            ["È disponibile WhisperLS "] = ["È disponibile WhisperLS ", "WhisperLS ", "WhisperLS ", "WhisperLS ", "WhisperLS "],
            ["Hai installato la versione "] = ["Hai installato la versione ", " is available. Installed version: ", " est disponible. Version installée : ", " está disponible. Versión instalada: ", " ist verfügbar. Installierte Version: "],
            ["Disinstallare "] = ["Disinstallare ", "Uninstall ", "Désinstaller ", "¿Desinstalar ", "Deinstallieren: "],
            ["Il modello verrà eliminato dalla cache locale e dovrà essere scaricato nuovamente per riutilizzarlo."] = ["Il modello verrà eliminato dalla cache locale e dovrà essere scaricato nuovamente per riutilizzarlo.", "The model will be removed from the local cache and must be downloaded again before reuse.", "Le modèle sera supprimé du cache local et devra être téléchargé à nouveau.", "El modelo se eliminará de la caché local y deberá descargarse de nuevo.", "Das Modell wird aus dem lokalen Cache entfernt und muss erneut heruntergeladen werden."],
            ["Sono già presenti: "] = ["Sono già presenti: ", "Already present: ", "Déjà présents : ", "Ya existen: ", "Bereits vorhanden: "],
            ["I file verranno sostituiti."] = ["I file verranno sostituiti.", "The files will be replaced.", "Les fichiers seront remplacés.", "Los archivos serán sustituidos.", "Die Dateien werden ersetzt."],
            ["inglese"] = ["inglese", "English only", "anglais uniquement", "solo inglés", "nur Englisch"],
            ["multilingua"] = ["multilingua", "multilingual", "multilingue", "multilingüe", "mehrsprachig"],
            ["Ultraleggero"] = ["Ultraleggero", "Ultra-light", "Ultra-léger", "Ultraligero", "Ultraleicht"],
            ["Leggero"] = ["Leggero", "Light", "Léger", "Ligero", "Leicht"],
            ["Compatto"] = ["Compatto", "Compact", "Compact", "Compacto", "Kompakt"],
            ["Bilanciato"] = ["Bilanciato", "Balanced", "Équilibré", "Equilibrado", "Ausgewogen"],
            ["Massima accuratezza"] = ["Massima accuratezza", "Maximum accuracy", "Précision maximale", "Máxima precisión", "Maximale Genauigkeit"],
            ["Cambia lingua interfaccia"] = ["Cambia lingua interfaccia", "Change interface language", "Changer la langue de l’interface", "Cambiar idioma de la interfaz", "Sprache der Benutzeroberfläche ändern"],
            ["Chiudi selettore lingua"] = ["Chiudi selettore lingua", "Close language selector", "Fermer le sélecteur de langue", "Cerrar selector de idioma", "Sprachauswahl schließen"],
            ["Potrai modificarla in qualsiasi momento dalla barra superiore."] = ["Potrai modificarla in qualsiasi momento dalla barra superiore.", "You can change it at any time from the top bar.", "Vous pourrez la modifier à tout moment depuis la barre supérieure.", "Podrás cambiarlo en cualquier momento desde la barra superior.", "Du kannst sie jederzeit über die obere Leiste ändern."],
            ["PROGRESSO"] = ["PROGRESSO", "PROGRESS", "PROGRESSION", "PROGRESO", "FORTSCHRITT"],
            ["DETTAGLI"] = ["DETTAGLI", "DETAILS", "DÉTAILS", "DETALLES", "DETAILS"],
            ["RIPRESA"] = ["RIPRESA", "RESUME", "REPRISE", "REANUDACIÓN", "FORTSETZUNG"],
            ["LINGUA"] = ["LINGUA", "LANGUAGE", "LANGUE", "IDIOMA", "SPRACHE"],
            ["PROGETTO"] = ["PROGETTO", "PROJECT", "PROJET", "PROYECTO", "PROJEKT"],
            ["RISULTATO"] = ["RISULTATO", "RESULT", "RÉSULTAT", "RESULTADO", "ERGEBNIS"],
            ["AVVIO"] = ["AVVIO", "START", "DÉMARRAGE", "INICIO", "START"],
            ["ERRORE"] = ["ERRORE", "ERROR", "ERREUR", "ERROR", "FEHLER"],
            ["AVVISO"] = ["AVVISO", "WARNING", "AVERTISSEMENT", "AVISO", "WARNUNG"],
            ["SUCCESSO"] = ["SUCCESSO", "SUCCESS", "SUCCÈS", "ÉXITO", "ERFOLG"],
            ["MODELLO"] = ["MODELLO", "MODEL", "MODÈLE", "MODELO", "MODELL"],
            ["PARTE"] = ["PARTE", "PART", "PARTIE", "PARTE", "TEIL"],
            ["CODA"] = ["CODA", "QUEUE", "FILE", "COLA", "WARTESCHLANGE"],
            ["INFO"] = ["INFO", "INFO", "INFO", "INFO", "INFO"],
            ["OUTPUT"] = ["OUTPUT", "OUTPUT", "SORTIE", "SALIDA", "AUSGABE"],
            ["Sbobinatura"] = ["Sbobinatura", "Transcription", "Transcription", "Transcripción", "Transkription"],
            ["Preparazione dei file del modello"] = ["Preparazione dei file del modello", "Preparing model files", "Préparation des fichiers du modèle", "Preparando archivos del modelo", "Modelldateien werden vorbereitet"],
            ["File del modello pronti"] = ["File del modello pronti", "Model files ready", "Fichiers du modèle prêts", "Archivos del modelo listos", "Modelldateien bereit"],
            ["Recupero automatico attivo"] = ["Recupero automatico attivo", "Automatic recovery active", "Récupération automatique active", "Recuperación automática activa", "Automatische Wiederherstellung aktiv"],
            ["Checkpoint temporaneo rimosso"] = ["Checkpoint temporaneo rimosso", "Temporary checkpoint removed", "Point de contrôle temporaire supprimé", "Punto de control temporal eliminado", "Temporärer Prüfpunkt entfernt"],
            ["traccia selezionata"] = ["traccia selezionata", "selected track", "piste sélectionnée", "pista seleccionada", "ausgewählte Spur"],
            ["tracce selezionate"] = ["tracce selezionate", "selected tracks", "pistes sélectionnées", "pistas seleccionadas", "ausgewählte Spuren"],
            ["modello persistente"] = ["modello persistente", "persistent model", "modèle persistant", "modelo persistente", "persistentes Modell"],
            ["segmenti"] = ["segmenti", "segments", "segments", "segmentos", "Segmente"],
            ["paragrafi"] = ["paragrafi", "paragraphs", "paragraphes", "párrafos", "Absätze"],
            ["completata"] = ["completata", "completed", "terminée", "completada", "abgeschlossen"],
            ["annullata"] = ["annullata", "cancelled", "annulée", "cancelada", "abgebrochen"],
            ["fallita"] = ["fallita", "failed", "échouée", "fallida", "fehlgeschlagen"],
            ["in corso"] = ["in corso", "in progress", "en cours", "en curso", "in Bearbeitung"],
            ["Output verificati; chiusura runtime non pulita"] = ["Output verificati; chiusura runtime non pulita", "Outputs verified; runtime shutdown was not clean", "Sorties vérifiées ; fermeture du runtime non propre", "Salidas verificadas; cierre del runtime no limpio", "Ausgaben geprüft; Runtime wurde nicht sauber beendet"],
            ["Controllo aggiornamenti non riuscito"] = ["Controllo aggiornamenti non riuscito", "Update check failed", "Échec de la recherche de mises à jour", "Falló la búsqueda de actualizaciones", "Updateprüfung fehlgeschlagen"],
            ["Modello già inizializzato: riuso"] = ["Modello già inizializzato: riuso", "Model already initialized: reusing", "Modèle déjà initialisé : réutilisation", "Modelo ya inicializado: reutilizando", "Modell bereits initialisiert: Wiederverwendung"],
            ["Errore durante"] = ["Errore durante", "Error while", "Erreur lors de", "Error durante", "Fehler bei"],
            ["Errore imprevisto"] = ["Errore imprevisto", "Unexpected error", "Erreur inattendue", "Error inesperado", "Unerwarteter Fehler"],
            ["Errore:"] = ["Errore:", "Error:", "Erreur :", "Error:", "Fehler:"],
            ["Warning:"] = ["Warning:", "Warning:", "Avertissement :", "Advertencia:", "Warnung:"]
        };

    private static IReadOnlyDictionary<string, string> BuildPortugueseStrings() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WhisperLS - Made with love ❤️"] = "WhisperLS - Feito com amor ❤️",
            ["Sistema offline integrato di trascrizione audio-to-text."] = "Sistema offline integrado de transcrição de áudio para texto.",
            ["FILE SORGENTE"] = "FICHEIROS DE ORIGEM",
            ["Nessun file selezionato"] = "Nenhum ficheiro selecionado",
            ["Scegli tracce"] = "Escolher faixas",
            ["Seleziona una o più tracce audio o video"] = "Selecionar uma ou mais faixas de áudio ou vídeo",
            ["Gestisci coda"] = "Gerir fila",
            ["Apri il manager della coda"] = "Abrir o gestor da fila",
            ["Impostazioni"] = "Definições",
            ["Configura la trascrizione selezionata."] = "Configurar a transcrição selecionada.",
            ["Modifica"] = "Editar",
            ["Modifica impostazioni"] = "Editar definições",
            ["Modello"] = "Modelo",
            ["Lingua"] = "Idioma da transcrição",
            ["Lingua dell'interfaccia"] = "Idioma da interface",
            ["Dispositivo"] = "Dispositivo",
            ["Automatico"] = "Automático",
            ["Rilevamento automatico"] = "Deteção automática",
            ["Filtro voce (VAD)"] = "Filtro de voz (VAD)",
            ["Timestamp per parola"] = "Marcas temporais por palavra",
            ["Attivato"] = "Ativado",
            ["Disattivato"] = "Desativado",
            ["Solo intervallo"] = "Apenas intervalo selecionado",
            ["Inizio intervallo"] = "Início do intervalo",
            ["Fine intervallo"] = "Fim do intervalo",
            ["Cartella di output"] = "Pasta de saída",
            ["Stessa cartella del file sorgente"] = "Mesma pasta do ficheiro de origem",
            ["Cambia"] = "Alterar",
            ["Fatto"] = "Concluído",
            ["Analisi in corso..."] = "A analisar…",
            ["Analisi del file e dell'hardware..."] = "A analisar ficheiros e hardware…",
            ["Durata sorgente"] = "Duração da origem",
            ["Tempo indicativo"] = "Tempo estimado",
            ["Non disponibile"] = "Não disponível",
            ["Stato"] = "Estado",
            ["Avvia trascrizione"] = "Iniciar transcrição",
            ["Trascrizione in corso..."] = "Transcrição em curso…",
            ["Riprendi"] = "Retomar",
            ["Annulla"] = "Cancelar",
            ["Ripristina coda"] = "Restaurar fila",
            ["Apri cartella output"] = "Abrir pasta de saída",
            ["Console"] = "Consola",
            ["Mostra"] = "Mostrar",
            ["Nascondi"] = "Ocultar",
            ["Mostra console"] = "Mostrar consola",
            ["Nascondi console"] = "Ocultar consola",
            ["In attesa di attività..."] = "A aguardar atividade…",
            ["Manager della coda"] = "Gestor da fila",
            ["Combina liberamente lavori singoli e trascrizioni multiparte."] = "Combine livremente trabalhos individuais e transcrições com várias partes.",
            ["Output"] = "Saída",
            ["Su"] = "Subir",
            ["Giù"] = "Descer",
            ["Unisci al precedente"] = "Juntar ao anterior",
            ["Separa"] = "Separar",
            ["Rimuovi"] = "Remover",
            ["Applica"] = "Aplicar",
            ["SINGOLO"] = "INDIVIDUAL",
            ["MULTIPARTE"] = "VÁRIAS PARTES",
            ["LAVORO"] = "TRABALHO",
            ["Model Manager"] = "Gestor de modelos",
            ["Disponibilità locale e appropriatezza per questo computer."] = "Disponibilidade local e adequação a este computador.",
            ["Rilevamento hardware..."] = "A detetar hardware…",
            ["Controllo dei modelli locali..."] = "A verificar modelos locais…",
            ["Appropriatezza"] = "Adequação",
            ["PRESENTE"] = "INSTALADO",
            ["NON PRESENTE"] = "NÃO INSTALADO",
            ["Pronto"] = "Pronto",
            ["Scarica"] = "Transferir",
            ["Disinstalla"] = "Desinstalar",
            ["Aggiorna"] = "Atualizar",
            ["Gestisci modelli"] = "Gerir modelos",
            ["Controlla aggiornamenti"] = "Procurar atualizações",
            ["Riduci a icona"] = "Minimizar",
            ["Massimizza"] = "Maximizar",
            ["Ripristina"] = "Restaurar",
            ["Chiudi"] = "Fechar",
            ["Chiudi impostazioni"] = "Fechar definições",
            ["Chiudi manager della coda"] = "Fechar gestor da fila",
            ["Chiudi Model Manager"] = "Fechar gestor de modelos",
            ["Avanzamento trascrizione"] = "Progresso da transcrição",
            ["Cronologia della console"] = "Histórico da consola",
            ["Informazioni sul dispositivo"] = "Informações sobre o dispositivo",
            ["Informazioni sul beam size"] = "Informações sobre beam size",
            ["Informazioni sul filtro voce"] = "Informações sobre o filtro de voz",
            ["Informazioni sui timestamp per parola"] = "Informações sobre marcas temporais por palavra",
            ["Nome output del lavoro"] = "Nome da saída do trabalho",
            ["Rimuovi lavoro dalla coda"] = "Remover trabalho da fila",
            ["Trascina verticalmente per riordinare"] = "Arrastar verticalmente para reordenar",
            ["Riprende la trascrizione dal checkpoint rimasto dopo un'interruzione"] = "Retomar a transcrição a partir do ponto de controlo guardado após uma interrupção",
            ["Ricarica la coda rimasta dopo un'interruzione"] = "Recarregar a fila guardada após uma interrupção",
            ["Genera un documento di testo semplice"] = "Criar um documento de texto simples",
            ["Genera sottotitoli con indicatori temporali"] = "Criar legendas com marcas temporais",
            ["Genera un documento Microsoft Word"] = "Criar um documento Microsoft Word",
            ["Trascrive soltanto la porzione compresa tra l'inizio e la fine indicate"] = "Transcrever apenas a parte entre o início e o fim indicados",
            ["La percentuale stima l'equilibrio fra velocità e risorse richieste."] = "A percentagem estima o equilíbrio entre velocidade e recursos necessários.",
            ["Stima indicativa: non è un benchmark."] = "Estimativa indicativa: não é um teste de desempenho.",
            ["Italiano"] = "Italiano",
            ["English"] = "Inglês",
            ["Français"] = "Francês",
            ["Español"] = "Espanhol",
            ["Deutsch"] = "Alemão",
            ["Português"] = "Português",
            ["Seleziona la lingua"] = "Escolha o idioma",
            ["Potrai modificarla in qualsiasi momento dalle impostazioni."] = "Pode alterá-lo a qualquer momento nas definições.",
            ["Continua"] = "Continuar",
            ["Aggiornamento disponibile"] = "Atualização disponível",
            ["Novità della versione"] = "Novidades da versão",
            ["Annullato"] = "Cancelado",
            ["Errore"] = "Erro",
            ["Avviso"] = "Aviso",
            ["Successo"] = "Sucesso",
            ["Parte"] = "Parte",
            ["Lavoro"] = "Trabalho",
            ["lavoro"] = "trabalho",
            ["lavori"] = "trabalhos",
            ["traccia"] = "faixa",
            ["tracce"] = "faixas",
            ["parti"] = "partes",
            ["attivo"] = "ativo",
            ["disattivo"] = "inativo",
            ["attivi"] = "ativos",
            ["disattivati"] = "desativados",
            ["Intervallo"] = "Intervalo",
            ["cartella del file sorgente"] = "pasta do ficheiro de origem",
            ["Gestisci coda · {0} · {1}"] = "Gerir fila · {0} · {1}",
            ["{0} · {1}"] = "{0} · {1}",
            ["Avvia coda ({0})"] = "Iniciar fila ({0})",
            ["Eccellente"] = "Excelente",
            ["Adatto"] = "Adequado",
            ["Discreto"] = "Razoável",
            ["Impegnativo"] = "Exigente",
            ["Poco adatto"] = "Pouco adequado",
            ["Coda"] = "Fila",
            ["Modello pronto. Puoi avviare la trascrizione."] = "Modelo pronto. Pode iniciar a transcrição.",
            ["Seleziona almeno un formato di output."] = "Selecione pelo menos um formato de saída.",
            ["Seleziona un file e un modello."] = "Selecione um ficheiro e um modelo.",
            ["Operazione annullata."] = "Operação cancelada.",
            ["La traccia audio selezionata è stata trascritta con successo!"] = "A faixa de áudio selecionada foi transcrita com sucesso!",
            ["Trascrizione completata"] = "Transcrição concluída",
            ["Trascrizione interrotta"] = "Transcrição interrompida",
            ["Scarica modello"] = "Transferir modelo",
            ["Coda in elaborazione..."] = "A processar fila…",
            ["Aggiornamento dei modelli locali..."] = "A atualizar modelos locais…",
            ["Controllo del modello locale..."] = "A verificar modelo local…",
            ["Download del modello..."] = "A transferir modelo…",
            ["Modello scaricato e verificato."] = "Modelo transferido e verificado.",
            ["Analisi non completata."] = "Análise não concluída.",
            ["Impossibile controllare gli aggiornamenti. Verifica la connessione."] = "Não foi possível procurar atualizações. Verifique a ligação.",
            ["Non ricordarmelo più per questa versione"] = "Não voltar a lembrar para esta versão",
            ["Consulta la release GitHub per i dettagli."] = "Consulte a versão no GitHub para obter detalhes.",
            ["Preparazione del download..."] = "A preparar transferência…",
            ["Aggiornamento di WhisperLS"] = "Atualização do WhisperLS",
            ["L'aggiornamento verrà ricordato alla prossima apertura."] = "A atualização será novamente apresentada no próximo arranque.",
            ["Download dell'aggiornamento annullato."] = "Transferência da atualização cancelada.",
            ["Download completato e verificato."] = "Transferência concluída e verificada.",
            ["Finestra principale non disponibile."] = "Janela principal indisponível.",
            ["Attendi il completamento della disinstallazione."] = "Aguarde pela conclusão da desinstalação.",
            ["Impossibile leggere lo stato dei modelli."] = "Não foi possível ler o estado dos modelos.",
            ["Il modello deve essere scaricato prima della trascrizione."] = "O modelo tem de ser transferido antes da transcrição.",
            ["Modello pronto. La stima è indicativa e non include l'avvio iniziale."] = "Modelo pronto. A estimativa é indicativa e não inclui o arranque inicial.",
            ["La cartella di output non è disponibile."] = "A pasta de saída não está disponível.",
            ["Il checkpoint non è più disponibile."] = "O ponto de controlo já não está disponível.",
            ["Ripresa della trascrizione..."] = "A retomar transcrição…",
            ["La trascrizione interrotta è stata ripresa e completata con successo!"] = "A transcrição interrompida foi retomada e concluída com sucesso!",
            ["Ripresa annullata. Il checkpoint è stato conservato."] = "Retoma cancelada. O ponto de controlo foi mantido.",
            ["Interrompere la coda?"] = "Interromper a fila?",
            ["Puoi saltare soltanto il lavoro corrente oppure annullare tutti i lavori rimanenti."] = "Pode ignorar apenas o trabalho atual ou cancelar todos os trabalhos restantes.",
            ["Solo lavoro corrente"] = "Apenas trabalho atual",
            ["Intera coda"] = "Toda a fila",
            ["La traccia corrente verrà interrotta; la coda proseguirà."] = "A faixa atual será interrompida; a fila continuará.",
            ["Coda interrotta ripristinata. Puoi riprendere l'elaborazione."] = "Fila interrompida restaurada. Pode retomar o processamento.",
            ["Sostituire la trascrizione esistente?"] = "Substituir a transcrição existente?",
            ["Sostituisci"] = "Substituir",
            ["Da 00:00:00"] = "De 00:00:00",
            ["A 00:00:00"] = "Até 00:00:00",
            ["È disponibile WhisperLS "] = "WhisperLS ",
            ["Hai installato la versione "] = " está disponível. Versão instalada: ",
            ["Disinstallare "] = "Desinstalar ",
            ["Il modello verrà eliminato dalla cache locale e dovrà essere scaricato nuovamente per riutilizzarlo."] = "O modelo será removido da cache local e terá de ser transferido novamente.",
            ["Sono già presenti: "] = "Já existem: ",
            ["I file verranno sostituiti."] = "Os ficheiros serão substituídos.",
            ["inglese"] = "apenas inglês",
            ["multilingua"] = "multilingue",
            ["Ultraleggero"] = "Ultraleve",
            ["Leggero"] = "Leve",
            ["Compatto"] = "Compacto",
            ["Bilanciato"] = "Equilibrado",
            ["Massima accuratezza"] = "Precisão máxima",
            ["Cambia lingua interfaccia"] = "Alterar idioma da interface",
            ["Chiudi selettore lingua"] = "Fechar seletor de idioma",
            ["Potrai modificarla in qualsiasi momento dalla barra superiore."] = "Pode alterá-lo a qualquer momento na barra superior.",
            ["PROGRESSO"] = "PROGRESSO",
            ["DETTAGLI"] = "DETALHES",
            ["RIPRESA"] = "RETOMA",
            ["LINGUA"] = "IDIOMA",
            ["PROGETTO"] = "PROJETO",
            ["RISULTATO"] = "RESULTADO",
            ["AVVIO"] = "INÍCIO",
            ["ERRORE"] = "ERRO",
            ["AVVISO"] = "AVISO",
            ["SUCCESSO"] = "SUCESSO",
            ["MODELLO"] = "MODELO",
            ["PARTE"] = "PARTE",
            ["CODA"] = "FILA",
            ["INFO"] = "INFO",
            ["OUTPUT"] = "SAÍDA",
            ["Sbobinatura"] = "Transcrição",
            ["Preparazione dei file del modello"] = "A preparar ficheiros do modelo",
            ["File del modello pronti"] = "Ficheiros do modelo prontos",
            ["Recupero automatico attivo"] = "Recuperação automática ativa",
            ["Checkpoint temporaneo rimosso"] = "Ponto de controlo temporário removido",
            ["traccia selezionata"] = "faixa selecionada",
            ["tracce selezionate"] = "faixas selecionadas",
            ["modello persistente"] = "modelo persistente",
            ["segmenti"] = "segmentos",
            ["paragrafi"] = "parágrafos",
            ["completata"] = "concluída",
            ["annullata"] = "cancelada",
            ["fallita"] = "falhada",
            ["in corso"] = "em curso",
            ["Output verificati; chiusura runtime non pulita"] = "Saídas verificadas; o runtime não terminou corretamente",
            ["Controllo aggiornamenti non riuscito"] = "Falha ao procurar atualizações",
            ["Modello già inizializzato: riuso"] = "Modelo já inicializado: a reutilizar",
            ["Errore durante"] = "Erro durante",
            ["Errore imprevisto"] = "Erro inesperado",
            ["Errore:"] = "Erro:",
            ["Warning:"] = "Aviso:"
        };

    private sealed record Preferences(string InterfaceLanguage);
}
