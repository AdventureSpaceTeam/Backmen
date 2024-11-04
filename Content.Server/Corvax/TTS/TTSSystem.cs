using System.Threading;
using System.Threading.Tasks;
using Content.Server.Chat.Systems;
using Content.Shared.Corvax.CCCVars;
using Content.Shared.Corvax.TTS;
using Content.Shared.GameTicking;
using Content.Shared.Radio;
using Content.Shared.Players.RateLimiting;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Corvax.TTS;

// ReSharper disable once InconsistentNaming
public sealed partial class TTSSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly TTSManager _ttsManager = default!;
    [Dependency] private readonly SharedTransformSystem _xforms = default!;
    [Dependency] private readonly IRobustRandom _rng = default!;

    private readonly List<string> _sampleText =
        new()
        {
            "Съешь же ещё этих мягких французских булок, да выпей чаю.",
            "Клоун, прекрати разбрасывать банановые кожурки офицерам под ноги!",
            "Капитан, вы уверены что хотите назначить клоуна на должность главы персонала?",
            "Эс Бэ! Тут человек в сером костюме, с тулбоксом и в маске! Помогите!!",
            "Учёные, тут странная аномалия в баре! Она уже съела мима!",
            "Я надеюсь что инженеры внимательно следят за сингулярностью...",
            "Вы слышали эти странные крики в техах? Мне кажется туда ходить небезопасно.",
            "Вы не видели Гамлета? Мне кажется он забегал к вам на кухню.",
            "Здесь есть доктор? Человек умирает от отравленного пончика! Нужна помощь!",
            "Вам нужно согласие и печать квартирмейстера, если вы хотите сделать заказ на партию дробовиков.",
            "Возле эвакуационного шаттла разгерметизация! Инженеры, нам срочно нужна ваша помощь!",
            "Бармен, налей мне самого крепкого вина, которое есть в твоих запасах!"
        };

    private const int MaxMessageChars = 100 * 2; // same as SingleBubbleCharLimit * 2
    private bool _isEnabled = false;

    public override void Initialize()
    {
        _cfg.OnValueChanged(CCCVars.TTSEnabled, v => _isEnabled = v, true);

        SubscribeLocalEvent<TransformSpeechEvent>(OnTransformSpeech);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);

        SubscribeLocalEvent<AnnouncementSpokeEvent>(OnAnnouncementSpoke); // TTS-Announce SS220
        SubscribeNetworkEvent<RequestPreviewTTSEvent>(OnRequestPreviewTTS);
        SubscribeLocalEvent<TTSComponent, EntitySpokeLanguageEvent>(OnEntitySpoke);

        RegisterRateLimits();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _ttsManager.ResetCache();
    }

    private async void OnRequestPreviewTTS(RequestPreviewTTSEvent ev, EntitySessionEventArgs args)
    {
        if (!_isEnabled ||
            !_prototypeManager.TryIndex<TTSVoicePrototype>(ev.VoiceId, out var protoVoice))
            return;

        var previewText = _rng.Pick(_sampleText);
        var soundData = await GenerateTTS(previewText, protoVoice.Speaker);
        if (soundData is null)
            return;

        RaiseNetworkEvent(new PlayTTSEvent(soundData), Filter.SinglePlayer(args.SenderSession));
    }

    private async void OnEntitySpoke(EntityUid uid, TTSComponent component, EntitySpokeLanguageEvent args)
    {
        var voiceId = component.VoicePrototypeId;
        if (!_isEnabled ||
            args.Message.Length > MaxMessageChars ||
            voiceId == null)
            return;

        var voiceEv = new TransformSpeakerVoiceEvent(uid, voiceId);
        RaiseLocalEvent(uid, voiceEv);
        voiceId = voiceEv.VoiceId;

        if (voiceId == null || !_prototypeManager.TryIndex(voiceId.Value, out var protoVoice))
            return;

        if (args.IsWhisper)
        {
            if (args.OrgMsg.Count > 0 || args.ObsMsg.Count > 0)
            {
                if(args.OrgMsg.Count > 0)
                    HandleWhisper(uid, args.Message, args.ObfuscatedMessage!, protoVoice.Speaker, args.OrgMsg);
                if(args.ObsMsg.Count > 0 && args is { LangMessage: not null, ObfuscatedLangMessage: not null })
                    HandleWhisper(uid, args.LangMessage, args.ObfuscatedLangMessage, protoVoice.Speaker, args.ObsMsg);

                return;
            }
            HandleWhisper(uid, args.Message, args.ObfuscatedMessage, protoVoice.Speaker, null);

            return;
        }

        if (args.OrgMsg.Count > 0 || args.ObsMsg.Count > 0)
        {
            if(args.OrgMsg.Count > 0)
                HandleSay(uid, args.Message, protoVoice.Speaker, args.OrgMsg);
            if(args.ObsMsg.Count > 0)
                HandleSay(uid, args.ObfuscatedMessage, protoVoice.Speaker, args.ObsMsg);
            return;
        }
        HandleSay(uid, args.Message, protoVoice.Speaker, null);
    }

    private async void HandleSay(EntityUid uid, string message, string speaker, Filter? filter)
    {
        var soundData = await GenerateTTS(message, speaker);
        if (soundData is null) return;
        RaiseNetworkEvent(new PlayTTSEvent(soundData, GetNetEntity(uid)), filter ?? Filter.Pvs(uid));
    }

    private async void HandleWhisper(EntityUid uid, string message, string obfMessage, string speaker, Filter? filter)
    {
        var netEntity = GetNetEntity(uid);

        PlayTTSEvent fullTtsEvent;
        PlayTTSEvent? obfTtsEvent = null;

        {
            var fullSoundData = await GenerateTTS(message, speaker, true);
            if (fullSoundData is null)
                return;

            fullTtsEvent = new PlayTTSEvent(fullSoundData, netEntity, true);
            if (message == obfMessage)
            {
                obfTtsEvent = fullTtsEvent;
            }
            else
            {
                var obfSoundData = await GenerateTTS(obfMessage, speaker, true);
                if (obfSoundData is not null)
                {
                    obfTtsEvent = new PlayTTSEvent(obfSoundData, netEntity, true);
                }
            }
        }

        // TODO: Check obstacles
        var xformQuery = GetEntityQuery<TransformComponent>();
        var sourcePos = _xforms.GetWorldPosition(xformQuery.GetComponent(uid), xformQuery);
        var receptions = (filter ?? Filter.Pvs(uid)).Recipients;
        foreach (var session in receptions)
        {
            if (!xformQuery.TryComp(session.AttachedEntity, out var xform))
                continue;

            var distance = (sourcePos - _xforms.GetWorldPosition(xform, xformQuery)).Length();
            if (distance > ChatSystem.VoiceRange * ChatSystem.VoiceRange)
                continue;

            if(distance <= ChatSystem.WhisperClearRange)
                RaiseNetworkEvent(fullTtsEvent, session);
            else if(obfTtsEvent!= null)
                RaiseNetworkEvent(obfTtsEvent, session);
        }
    }


    private readonly Dictionary<string, Task<byte[]?>> _ttsTasks = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ReSharper disable once InconsistentNaming
    private async Task<byte[]?> GenerateTTS(string text, string speaker, bool isWhisper = false)
    {
        var textSanitized = Sanitize(text);
        if (textSanitized == "") return null;
        if (char.IsLetter(textSanitized[^1]))
            textSanitized += ".";

        // c4llv07e fix tts start
        // var ssmlTraits = SoundTraits.RateFast;
        // if (isWhisper)
        //     ssmlTraits = SoundTraits.PitchVerylow;
        // var textSsml = ToSsmlText(textSanitized, ssmlTraits);
        // c4llv07e fix tts end

        return await _ttsManager.ConvertTextToSpeech(speaker, textSanitized); // c4llv07e fix tts
    }
}

public sealed class EntitySpokeLanguageEvent: EntityEventArgs
{
    public readonly string? ObfuscatedLangMessage;
    public readonly string? LangMessage;
    public readonly bool IsWhisper;
    public readonly Filter OrgMsg;
    public readonly Filter ObsMsg;
    public readonly EntityUid Source;
    public readonly string Message;
    public readonly string OriginalMessage;
    public readonly string ObfuscatedMessage; // not null if this was a whisper

    /// <summary>
    ///     If the entity was trying to speak into a radio, this was the channel they were trying to access. If a radio
    ///     message gets sent on this channel, this should be set to null to prevent duplicate messages.
    /// </summary>
    public RadioChannelPrototype? Channel;

    public EntitySpokeLanguageEvent(
        Filter orgMsg,
        Filter obsMsg,
        EntityUid source,
        string message,
        string originalMessage,
        RadioChannelPrototype? channel,
        bool isWhisper,
        string obfuscatedMessage,
        string? langMessage = null,
        string? obfuscatedLangMessage = null)
    {
        ObfuscatedLangMessage = obfuscatedLangMessage;
        LangMessage = langMessage;
        IsWhisper = isWhisper;
        OrgMsg = orgMsg;
        ObsMsg = obsMsg;
        Source = source;
        Message = message;
        OriginalMessage = originalMessage; // Corvax-TTS: Spec symbol sanitize
        Channel = channel;
        ObfuscatedMessage = obfuscatedMessage;
    }
}

public sealed class TransformSpeakerVoiceEvent : EntityEventArgs
{
    public EntityUid Sender;
    public string VoiceId;

    public TransformSpeakerVoiceEvent(EntityUid sender, string voiceId)
    {
        Sender = sender;
        VoiceId = voiceId;
    }
}
