using System;
using System.Collections;
using System.Threading.Tasks;
using TwitchLib.Api.Helix;
using TwitchLib.Api.Helix.Models.ChannelPoints;
using TwitchLib.Api.Helix.Models.Users.GetUsers;
using TwitchLib.Client.Enums;
using TwitchLib.PubSub.Events;
using TwitchLib.PubSub.Models.Responses.Messages;
using TwitchLib.PubSub.Models.Responses.Messages.Redemption;
using TwitchLib.Unity;
using UnityEngine;
using UnityEngine.ProBuilder.MeshOperations;
using SubscriptionPlan = TwitchLib.PubSub.Enums.SubscriptionPlan;
public enum BidType { ChannelPoints, Bits, NewPlayerBonus, NewSubBonus}
public class TwitchPubSub : MonoBehaviour
{
    [SerializeField] private GameManager _gm;
    [SerializeField] private AutoPredictions _autoPredictions;
    [SerializeField] private BidHandler _ticketHandler;
    [SerializeField] private TwitchClient _twitchClient;
    [SerializeField] private BitTrigger _lavaBitTrigger;
    [SerializeField] private BitTrigger _waterBitTrigger;
    [SerializeField] private RebellionController _rebellionController;

    private PubSub _pubSub;

    public void Init(string channelID, string botAccessToken)
    {
        if (_pubSub != null)
            _pubSub.Disconnect(); 

        // Create new instance of PubSub Client
        _pubSub = new PubSub();

        // Connect and listen for events
        _pubSub.OnWhisper += OnWhisper;
        _pubSub.OnPubSubServiceConnected += OnPubSubServiceConnected;
        _pubSub.OnChannelPointsRewardRedeemed += OnChannelPointsRedeemed;
        _pubSub.OnRewardRedeemed += OnRewardRedeemed;
        _pubSub.OnBitsReceivedV2 += OnBitsReceivedV2;
        _pubSub.OnChannelSubscription += OnChannelSubscription;
                
        _pubSub.Connect();

        _pubSub.ListenToChannelPoints(channelID);
        _pubSub.ListenToWhispers(channelID);
        _pubSub.ListenToBitsEventsV2(channelID);
        _pubSub.ListenToSubscriptions(channelID);

        _pubSub.SendTopics(botAccessToken);
        Debug.Log($"Done Initializing PubSub");
    }
    private void OnPubSubServiceConnected(object sender, EventArgs e)
    {
        Debug.Log("Connected to Twitch PubSub!");
    }

    private void OnChannelPointsRedeemed(object sender, TwitchLib.PubSub.Events.OnChannelPointsRewardRedeemedArgs e)
    {
        Redemption redemption = e.RewardRedeemed.Redemption;
       
        var user = redemption.User;
        string rewardID = redemption.Reward.Id;
        string redemptionID = redemption.Id;
        string rewardTitle = redemption.Reward.Title;

        Debug.Log($"reward redeemed {e.ChannelId} rewardID: {rewardID} redemptionID: {redemptionID}"); 

        StartCoroutine(HandleOnChannelPointsRedeemed(user.Id, user.Login, rewardTitle, redemption.UserInput, redemption.Reward.Cost)); 
    }
    public IEnumerator HandleOnChannelPointsRedeemed(string twitchId, string twitchUsername, string rewardTitle, string msg, int cost)
    {
        //Get the player handler of the player redeeming tickets
        CoroutineResult<PlayerHandler> coResult = new CoroutineResult<PlayerHandler>();
        yield return _gm.GetPlayerHandler(twitchId, coResult);

        PlayerHandler ph = coResult.Result;
        if (ph == null)
        {
            Debug.LogError("Failed to find player handler");
            yield break;
        }

        ph.pp.LastInteraction = DateTime.Now;
        ph.pp.TwitchUsername = twitchUsername;
        ph.pp.TotalTicketsSpent += cost; 

        if (rewardTitle.StartsWith("Activate Lava"))
            _lavaBitTrigger.AddBits(twitchUsername, AppConfig.inst.GetI("ThroneLavaCost"));
        else if (rewardTitle.StartsWith("Activate Water"))
            _waterBitTrigger.AddBits(twitchUsername, AppConfig.inst.GetI("ThroneWaterCost"));
        else
            _ticketHandler.BidRedemption(ph, cost, BidType.ChannelPoints);

    }

    private void OnBitsReceivedV2(object sender, OnBitsReceivedV2Args e)
    {
        Debug.Log($"Inside bits received v2 total bits: {e.TotalBitsUsed} {e.BitsUsed}");
        //Bits used is the amount contained in the message, total bits sums up the total bits the user has donated over time. Not sure over what timespan.
        

        StartCoroutine(HandleOnBitsReceived(e.UserId, e.UserName, e.ChatMessage, e.BitsUsed)); 
    }

    public IEnumerator HandleOnBitsReceived(string twitchId, string twitchUsername, string rawMsg, int bitsInMessage)
    {
        CLDebug.Inst.ReportDonation("NUEVA DONACIÓN:", $"{bitsInMessage} bits (${bitsInMessage/100f}) de {twitchUsername}.\nMensaje: {rawMsg}"); 

        CoroutineResult<PlayerHandler> coResult = new CoroutineResult<PlayerHandler>();
        yield return _gm.GetPlayerHandler(twitchId, coResult);

        PlayerHandler ph = coResult.Result;
        if (ph == null)
        {
            Debug.LogError("Failed to find player handler");
            yield break;
        }

        ph.pp.LastInteraction = DateTime.Now;
        ph.pp.TwitchUsername = twitchUsername;

        if (rawMsg.ToLower().Contains("!lava"))
        {
            _lavaBitTrigger.AddBits(twitchUsername, bitsInMessage);
            yield break;
        }
        if (rawMsg.ToLower().Contains("!agua"))
        {
            _waterBitTrigger.AddBits(twitchUsername, bitsInMessage);
            
            yield break;
        }

        if(bitsInMessage >= 200)
            StartCoroutine(_rebellionController.CreateRebellion(ph, bitsInMessage, rawMsg));
        else
            _twitchClient.PingReplyPlayer(twitchUsername, "Rebelion como míínimo necesitas 200 bits. Cada 100 bits en un mensaje incrementa por 1.");
        

        _ticketHandler.BidRedemption(ph, bitsInMessage, BidType.Bits);
    }

    private void OnRewardRedeemed(object sender, TwitchLib.PubSub.Events.OnRewardRedeemedArgs e)
    {
        Debug.Log($"reward redeemed: {e.RewardTitle} {e.RewardCost} message {e.Message}"); 
    }

    private void OnChannelSubscription(object sender, OnChannelSubscriptionArgs e)
    {
        if (!AppConfig.inst.GetB("EnableNewSubTrigger"))
            return;

        var subscription = e.Subscription;

        string twitchId = subscription.UserId;
        string username = subscription.Username;

        int MultiMonthDuration = 1;
        if(e.Subscription.MultiMonthDuration.HasValue && e.Subscription.MultiMonthDuration.Value >= 1)
            MultiMonthDuration = e.Subscription.MultiMonthDuration.Value;

        var subPlan = subscription.SubscriptionPlan;

        if (subscription.IsGift.HasValue)
        {
            if(subscription.IsGift.Value)
            {
                string recipientId = subscription.RecipientId;
                string recipientUsername = subscription.RecipientName;
                StartCoroutine(HandleGiftSubscription(twitchId, username, recipientId, recipientUsername, MultiMonthDuration, subPlan));
                return;
            }
        }

        StartCoroutine(HandleOnSubscription(twitchId, username, MultiMonthDuration, subPlan));
    }

    public IEnumerator HandleOnSubscription(string twitchId, string username, int MultiMonthDuration, SubscriptionPlan subPlan)
    {
        CLDebug.Inst.ReportDonation("NUEVA SUB", $"{username} se ha suscrito durante {MultiMonthDuration} {subPlan}");

        CoroutineResult<PlayerHandler> coResult = new CoroutineResult<PlayerHandler>();
        yield return _gm.GetPlayerHandler(twitchId, coResult);

        PlayerHandler ph = coResult.Result;
        if (ph == null)
        {
            Debug.LogError("Failed to find player handler");
            yield break;
        }

        ph.pp.LastInteraction = DateTime.Now;
        ph.pp.TwitchUsername = username;

        MyTTS.inst.Announce($"{username} se ha suscrito durante {MultiMonthDuration} mes{((MultiMonthDuration > 1) ? "es" : "")} con {subPlan}. Brofist.");

        int bidAmount = AppConfig.inst.GetI("NewSubBonusBid") * MultiMonthDuration;
        if (subPlan == SubscriptionPlan.Tier2)
            bidAmount *= 2;
        else if(subPlan == SubscriptionPlan.Tier3)
            bidAmount *= 3;

        _ticketHandler.BidRedemption(ph, bidAmount, BidType.NewSubBonus);
    }

    public IEnumerator HandleGiftSubscription(string twitchId, string username, string recipientId, string recipientUsername, int MultiMonthDuration, SubscriptionPlan subPlan)
    {
        CLDebug.Inst.ReportDonation("NUEVA SUB REGALADA", $"{username} ha regalado {recipientUsername} {MultiMonthDuration} {subPlan}");

        CoroutineResult<PlayerHandler> coResult = new CoroutineResult<PlayerHandler>();
        yield return _gm.GetPlayerHandler(twitchId, coResult);

        PlayerHandler ph = coResult.Result;
        if (ph == null)
        {
            Debug.LogError("Failed to find player handler");
            yield break;
        }

        ph.pp.LastInteraction = DateTime.Now;
        ph.pp.TwitchUsername = username;

        MyTTS.inst.AggregateSubGift(username, MultiMonthDuration, subPlan); 
        //MyTTS.inst.Announce($"{username} gifted {MultiMonthDuration} {subPlan} sub{((MultiMonthDuration > 1) ? "s" : "")} to {recipientUsername}. What a bro.");

        int bidAmount = AppConfig.inst.GetI("NewSubBonusBid") * MultiMonthDuration;
        if (subPlan == SubscriptionPlan.Tier2)
            bidAmount *= 2;
        else if (subPlan == SubscriptionPlan.Tier3)
            bidAmount *= 3;

        _ticketHandler.BidRedemption(ph, bidAmount, BidType.NewSubBonus);
    }

    private void OnWhisper(object sender, TwitchLib.PubSub.Events.OnWhisperArgs e)
    {
        Debug.Log($"{e.Whisper.Data}");
        // Do your bits logic here.
    }

    private void OnDestroy()
    {
        // Cleanup when the object is destroyed
        if(_pubSub != null )
            _pubSub.Disconnect();
    }
}