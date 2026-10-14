using BatomonHelper.Simulation;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BatomonHelper.Training;

public sealed record PlayActionRow(string Kind,string Description,string? Icon,SimAction Action);
public sealed record PlaySessionRow(string Id,string Trainer,int Wins,int TurnsRemaining,int Day,bool InPool,bool Enabled);

public partial class MainWindow
{
    private PlaySession? _play;
    private bool _playBusy;
    private int _selectedShopSource=-1;
    private Point _playDragStart;
    private int _playDragSource=-1;
    private bool _playDragging;
    private string _playInfo="Start a session to play against independent simulated opponents.",_playInventory="",_playResult="",_playInteractionHint="Merging is automatic: keep the required matching creatures on the team or bench. Click a shop offer to buy it, or drag a team / bench card to move or swap it.";
    public string PlayInfo {get=>_playInfo;set=>Set(ref _playInfo,value);}
    public string PlayInventory {get=>_playInventory;set=>Set(ref _playInventory,value);}
    public string PlayResult {get=>_playResult;set=>Set(ref _playResult,value);}
    public string PlayInteractionHint {get=>_playInteractionHint;set=>Set(ref _playInteractionHint,value);}
    public ObservableCollection<HistoryCard> PlayTeam {get;}=[];
    public ObservableCollection<HistoryCard> PlayBench {get;}=[];
    public ObservableCollection<HistoryCard> PlayShop {get;}=[];
    public ObservableCollection<PlayActionRow> PlayActions {get;}=[];
    public ObservableCollection<PlaySessionRow> PlaySessions {get;}=[];
    private UserSessionStore SessionStore=>new(Path.Combine(_root,"work","user-sessions"),_rules);
    public void RenderPlayPreview(string path)
    {
        _play=new PlaySession(_rules,69,_rules.Trainers.FirstOrDefault(x=>x.Id=="youngster-m")?.Id??_rules.Trainers[0].Id);
        for(var i=0;i<8;i++){
            var legal=_play.Environment.GetLegalActions();var action=legal.FirstOrDefault(x=>x.Kind==Move.Choose)??legal.FirstOrDefault(x=>x.Kind==Move.Buy&&x.Target>=0&&x.Target<6);
            if(action is null)break;_play.Apply(action);
        }
        PlayTrainer.SelectedItem=_rules.Trainers.First(x=>x.Id==_play.Session.Trainer);Tabs.SelectedIndex=2;DisplayPlay();RenderWindow(path);
    }
    private void InitializePlay()
    {
        PlayTrainer.ItemsSource=_rules.Trainers;PlayTrainer.SelectedIndex=0;
        RefreshPlaySessions();
    }
    private async void NewPlaySession(object sender,RoutedEventArgs e)
    {
        if(_playBusy)return;
        try{
            _playBusy=true;_rules.RequireReady();var seed=ulong.Parse(PlaySeed.Text);
            var trainer=((InventoryDefinition)PlayTrainer.SelectedItem).Id;
            if(_play is not null)SessionStore.Save(_play.Session);
            PlayInfo="Preparing independent opponents…";
            _play=await Task.Run(()=>new PlaySession(_rules,seed,trainer));
            SessionStore.Save(_play.Session);PlayResult="";_selectedShopSource=-1;DisplayPlay();RefreshPlaySessions();
        }catch(Exception error){Status=error.Message;}finally{_playBusy=false;}
    }
    private void DisplayPlay()
    {
        if(_play is null)return;var s=_play.Environment.State;
        PlayInfo=$"Turn {s.Day}/{_rules.Parameters.EffectiveTurnCap} · {RemainingTurns(s)} turns remaining · rank {s.Rank} · ${s.Gold:0.##} · {s.Wins}/10 wins · HP {s.MaxHp:0} · {s.Phase}\n"+
            $"{s.Trainer} · item uses {s.ItemUses}/{s.MaximumItemUses} · free rerolls {s.FreeRerolls} · {(s.Locked?"shop locked":"shop unlocked")}";
        PlayTeam.Clear();PlayBench.Clear();PlayShop.Clear();
        for(var i=0;i<6;i++)PlayTeam.Add(PlayCard(s.Units[i],i,"board"));
        for(var i=6;i<10;i++)PlayBench.Add(PlayCard(s.Units[i],i,"board"));
        for(var i=0;i<5;i++){
            var offer=s.PendingShopStart?null:s.Shop[i];
            if(offer?.Creature is {} unit){var card=PlayCard(unit,i,"shop");PlayShop.Add(card with {Name=card.Name+$" · ${offer.Price:0.##}"});}
            else if(offer?.ItemId is {} item){var def=_rules.Items.First(x=>x.Id==item);PlayShop.Add(new($"{i+1}. {def.Name} · ${offer.Price:0.##}","Item","",def.Description,ItemSprite(def.Id),i,false,"shop"));}
            else PlayShop.Add(new($"{i+1}. Empty","","","",null,i,false,"shop"));
        }
        PlayInventory="Trinkets: "+string.Join(" · ",s.Bag.Select(x=>$"{_rules.Trinkets.First(t=>t.Id==x.Id).Name} ×{x.Quantity}"))+
            "\nExtra trainers: "+string.Join(" · ",s.ExtraTrainers)+
            (s.Overflow is {} overflow?$"\nOverflow: {overflow.SpeciesId} (source 11)":"")+
            (s.CurrentEvent is {} current?$"\nEncounter: {current}":"");
        if(_selectedShopSource>=0 && !_play.Environment.GetLegalActions().Any(x=>(x.Kind is Move.Buy or Move.BuyItem) && x.Source==_selectedShopSource))_selectedShopSource=-1;
        RefreshPlayActions();
    }
    private HistoryCard PlayCard(Unit? unit,int slot,string zone)
    {
        var previous=_historyRules;try{_historyRules=_rules;var card=Card(unit,slot);
            if(unit is null)return card with {Slot=slot,Zone=zone};
            var ability=LevelRules.Resolve(_rules.Species.First(x=>x.Id==unit.SpeciesId),unit.Level,unit.Shiny).Description;
            return card with {Stats=card.Stats+"\n"+ability,Slot=slot,IsOccupied=true,Zone=zone};
        }finally{_historyRules=previous;}
    }
    private void RefreshPlayActions()
    {
        PlayActions.Clear();if(_play is null||_play.Environment.State.Phase==Phase.Complete)return;
        var state=_play.Environment.State;
        foreach(var action in _play.Environment.GetLegalActions().Where(x=>x.Kind is not (Move.Buy or Move.BuyItem or Move.Move))){
            var text=action.Kind switch {
                Move.Buy=>$"Buy shop {action.Source+1} → {(action.Target>=0?$"slot {action.Target+1}":"overflow")}",
                Move.BuyItem=>$"Use item from shop {action.Source+1}",Move.Sell=>$"Sell slot {action.Source+1}",
                Move.Move=>$"Move / swap slot {action.Source+1} → {action.Target+1}",
                Move.Choose=>DescribeChoice(state.Choices[action.Choice])+(action.Target<0?"":$" → slot {action.Target+1}"),
                Move.Lock=>state.Locked?"Unlock shop":"Lock shop",Move.Reroll=>"Reroll shop",_=>"Finish shopping and battle"};
            PlayActions.Add(new(action.Kind.ToString(),text,action.Kind==Move.Choose?ChoiceIcon(state.Choices[action.Choice]):null,action));
        }
    }
    private async void PlayActionButtonClick(object sender,RoutedEventArgs e)
    {
        if(sender is not Button {Tag:SimAction action})return;
        await ApplyPlayAction(action);
    }
    private async Task ApplyPlayAction(SimAction action)
    {
        if(_play is null||_playBusy)return;
        try{
            _playBusy=true;var step=await Task.Run(()=>_play.Apply(action));
            if(step.Battle is {} battle)PlayResult=$"{(battle.PlayerWon?"Win":"Loss")} · {battle.Seconds:0.##}s · HP {battle.PlayerHp:0.##} vs {battle.OpponentHp:0.##}";
            _selectedShopSource=-1;PlayInteractionHint="Merging is automatic: keep the required matching creatures on the team or bench. Click a shop offer to buy it, or drag a team / bench card to move or swap it.";
            SessionStore.Save(_play.Session);DisplayPlay();RefreshPlaySessions();
        }catch(Exception error){Status=error.Message;}finally{_playBusy=false;}
    }
    private void PlayCardMouseDown(object sender,MouseButtonEventArgs e)
    {
        _playDragSource=-1;
        if(sender is Border {DataContext:HistoryCard card} && card.Zone=="board" && card.IsOccupied)
        {
            _playDragStart=e.GetPosition((IInputElement)sender);_playDragSource=card.Slot;
        }
    }
    private void PlayCardMouseMove(object sender,MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed||_playDragSource<0||sender is not Border border)return;
        var point=e.GetPosition(border);
        if(Math.Abs(point.X-_playDragStart.X)<SystemParameters.MinimumHorizontalDragDistance &&
           Math.Abs(point.Y-_playDragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        var source=_playDragSource;_playDragSource=-1;
        _playDragging=true;
        try{DragDrop.DoDragDrop(border,source,DragDropEffects.Move);}finally{_playDragging=false;}
    }
    private void PlayCardMouseUp(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left||_playDragging||sender is not Border border||border.DataContext is not HistoryCard card)return;
        if(card.Zone=="shop")
        {
            PlayShopCardClick(card);e.Handled=true;return;
        }
        if(card.Zone=="board"&&_selectedShopSource>=0&&!card.IsOccupied)
        {
            var action=_play?.Environment.GetLegalActions().FirstOrDefault(x=>x.Kind==Move.Buy&&x.Source==_selectedShopSource&&x.Target==card.Slot);
            if(action is not null){_ = ApplyPlayAction(action);e.Handled=true;}
        }
    }
    private void PlayCardDragOver(object sender,DragEventArgs e)
    {
        e.Effects=DragDropEffects.None;
        if(sender is Border {DataContext:HistoryCard target} && target.Zone=="board" && e.Data.GetDataPresent(typeof(int)))
        {
            var source=(int)e.Data.GetData(typeof(int))!;
            if(FindMoveAction(source,target.Slot) is not null)e.Effects=DragDropEffects.Move;
        }
        e.Handled=true;
    }
    private void PlayCardDrop(object sender,DragEventArgs e)
    {
        if(sender is not Border {DataContext:HistoryCard target}||target.Zone!="board"||!e.Data.GetDataPresent(typeof(int)))return;
        var source=(int)e.Data.GetData(typeof(int))!;
        if(FindMoveAction(source,target.Slot) is {} action){_ = ApplyPlayAction(action);e.Handled=true;}
    }
    private SimAction? FindMoveAction(int source,int target)
    {
        if(_play is null||source==target)return null;
        return _play.Environment.GetLegalActions().FirstOrDefault(x=>x.Kind==Move.Move&&
            ((x.Source==source&&x.Target==target)||(x.Source==target&&x.Target==source)));
    }
    private void PlayShopCardClick(HistoryCard card)
    {
        if(_play is null||_playBusy)return;
        var actions=_play.Environment.GetLegalActions().Where(x=>(x.Kind is Move.Buy or Move.BuyItem)&&x.Source==card.Slot).ToArray();
        if(actions.Length==0)
        {
            PlayInteractionHint=card.Name.EndsWith("Empty",StringComparison.Ordinal)?"That shop slot is empty.":"That offer is not affordable or cannot be used right now.";
            return;
        }
        if(actions.Any(x=>x.Kind==Move.BuyItem))
        {
            _ = ApplyPlayAction(actions.Single(x=>x.Kind==Move.BuyItem));return;
        }
        if(actions.Length==1){_ = ApplyPlayAction(actions[0]);return;}
        _selectedShopSource=card.Slot;
        PlayInteractionHint=$"Shop {card.Slot+1} selected. Click an empty team or bench slot to buy it there.";
    }
    private void SavePlaySession(object sender,RoutedEventArgs e)
    {try{if(_play is not null){SessionStore.Save(_play.Session);RefreshPlaySessions();Status="Playable session saved.";}}catch(Exception error){Status=error.Message;}}
    private void RefreshPlaySessions()
    {
        PlaySessions.Clear();foreach(var s in SessionStore.List())PlaySessions.Add(new(s.Id,s.Trainer,s.Current.Wins,RemainingTurns(s.Current),s.Current.Day,s.InOpponentPool,s.Enabled));
    }
    private async void ResumePlaySession(object sender,RoutedEventArgs e)
    {
        if(_playBusy||PlaySessionList.SelectedItem is not PlaySessionRow row)return;
        try{_playBusy=true;var store=SessionStore;var session=store.List().Single(x=>x.Id==row.Id);
            if(_play is not null)store.Save(_play.Session);
            _play=await Task.Run(()=>new PlaySession(_rules,session,store));DisplayPlay();
        }catch(Exception error){Status=error.Message;}finally{_playBusy=false;}
    }
    private void InspectPlaySession(object sender,RoutedEventArgs e)
    {
        if(PlaySessionList.SelectedItem is not PlaySessionRow row)return;
        try{var session=SessionStore.List().Single(x=>x.Id==row.Id);_historyRules=_rules;_day=0;
            _selected=new(new(session.Id,-1,-1,session.Trainer,session.Current.Wins,RemainingTurns(session.Current),session.Current.Day,
                session.Current.Wins>=10,false,DateTimeOffset.UtcNow,session.RulesHash),session.Days);
            SelectedRunLabel=$"Your session · {session.Trainer} · {session.Decisions.Length} actions";Tabs.SelectedIndex=1;DisplayDay();
        }catch(Exception error){Status=error.Message;}
    }
    private void ChangePool(object sender,RoutedEventArgs e)
    {
        if(_playBusy||PlaySessionList.SelectedItem is not PlaySessionRow row)return;
        try{var store=SessionStore;var session=store.List().Single(x=>x.Id==row.Id);var command=((Button)sender).Tag?.ToString();
            var changed=command switch {"add"=>session with {InOpponentPool=true},"remove"=>session with {InOpponentPool=false},_=>session with {Enabled=!session.Enabled}};
            changed=store.SetPool(changed.Id,changed.InOpponentPool,changed.Enabled);
            if(_play?.Session.Id==changed.Id)_play.SetPool(changed.InOpponentPool,changed.Enabled);RefreshPlaySessions();
            Status="Opponent pool updated. Training and evaluation load it at the next launch.";
        }catch(Exception error){Status=error.Message;}
    }
    private string? ItemSprite(string id)
    {
        var path=Path.Combine(_root,"assets","wiki","items",id+".png");
        return File.Exists(path)?path:null;
    }
    private int RemainingTurns(RunState state) => state.Phase==Phase.Complete?0:Math.Max(0,_rules.Parameters.EffectiveTurnCap-state.Day+1);
    private string? ChoiceIcon(Choice choice)
    {
        var id=choice.DefinitionId;
        if(id is null)return null;
        var item=_rules.Trinkets.Concat(_rules.Items).FirstOrDefault(x=>x.Id==id);
        if(item is not null){var folder=_rules.Trinkets.Any(x=>x.Id==id)?"trinkets":"items";var path=Path.Combine(_root,"assets","wiki",folder,id+".png");return File.Exists(path)?path:null;}
        var trainer=_rules.Trainers.FirstOrDefault(x=>x.Id==id);
        if(trainer is not null){var path=Path.Combine(_root,"assets","wiki","trainers",id+".png");return File.Exists(path)?path:null;}
        return null;
    }
    private static string DescribeChoice(Choice choice)
    {
        var details=choice.Effects.Select(effect=>effect.Operation switch {
            Operation.Gold=>$"gain ${effect.Amount:0.##}", Operation.GrantTrinket=>"gain a trinket", Operation.GrantUnit=>"gain a creature",
            Operation.Add=>$"give {effect.Target} +{effect.Amount:0.##} {effect.Stat}", Operation.LevelUp=>$"level up an eligible creature",
            Operation.ShopRank=>"increase shop rank", _=>effect.Operation.ToString()}).ToArray();
        return details.Length==0?choice.Label:$"{choice.Label} — {string.Join(", ",details)}";
    }
}
