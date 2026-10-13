using BatomonHelper.Simulation;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BatomonHelper.Training;

public sealed record PlayActionRow(string Kind,string Description,SimAction Action);
public sealed record PlaySessionRow(string Id,string Trainer,int Wins,int Lives,int Day,bool InPool,bool Enabled);

public partial class MainWindow
{
    private PlaySession? _play;
    private bool _playBusy;
    private string _playInfo="Start a session to play against independent simulated opponents.",_playInventory="",_playResult="";
    public string PlayInfo {get=>_playInfo;set=>Set(ref _playInfo,value);}
    public string PlayInventory {get=>_playInventory;set=>Set(ref _playInventory,value);}
    public string PlayResult {get=>_playResult;set=>Set(ref _playResult,value);}
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
        PlayKind.ItemsSource=new[]{"All"}.Concat(Enum.GetNames<Move>()).ToArray();PlayKind.SelectedIndex=0;
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
            SessionStore.Save(_play.Session);PlayResult="";DisplayPlay();RefreshPlaySessions();
        }catch(Exception error){Status=error.Message;}finally{_playBusy=false;}
    }
    private void DisplayPlay()
    {
        if(_play is null)return;var s=_play.Environment.State;
        PlayInfo=$"Day {s.Day} · rank {s.Rank} · ${s.Gold:0.##} · {s.Wins}/10 wins · {s.Lives} lives · HP {s.MaxHp:0} · {s.Phase}\n"+
            $"{s.Trainer} · item uses {s.ItemUses}/{s.MaximumItemUses} · free rerolls {s.FreeRerolls} · {(s.Locked?"shop locked":"shop unlocked")}";
        PlayTeam.Clear();PlayBench.Clear();PlayShop.Clear();
        for(var i=0;i<6;i++)PlayTeam.Add(PlayCard(s.Units[i],i));
        for(var i=6;i<10;i++)PlayBench.Add(PlayCard(s.Units[i],i));
        for(var i=0;i<5;i++){
            var offer=s.PendingShopStart?null:s.Shop[i];
            if(offer?.Creature is {} unit){var card=PlayCard(unit,i);PlayShop.Add(card with {Name=card.Name+$" · ${offer.Price:0.##}"});}
            else if(offer?.ItemId is {} item){var def=_rules.Items.First(x=>x.Id==item);PlayShop.Add(new($"{i+1}. {def.Name} · ${offer.Price:0.##}","Item","",def.Description,null));}
            else PlayShop.Add(new($"{i+1}. Empty","","","",null));
        }
        PlayInventory="Trinkets: "+string.Join(" · ",s.Bag.Select(x=>$"{_rules.Trinkets.First(t=>t.Id==x.Id).Name} ×{x.Quantity}"))+
            "\nExtra trainers: "+string.Join(" · ",s.ExtraTrainers)+
            (s.Overflow is {} overflow?$"\nOverflow: {overflow.SpeciesId} (source 11)":"")+
            (s.CurrentEvent is {} current?$"\nEncounter: {current}":"");
        RefreshPlayActions();
    }
    private HistoryCard PlayCard(Unit? unit,int slot)
    {
        var previous=_historyRules;try{_historyRules=_rules;var card=Card(unit,slot);
            if(unit is null)return card;
            var ability=LevelRules.Resolve(_rules.Species.First(x=>x.Id==unit.SpeciesId),unit.Level,unit.Shiny).Description;
            return card with {Stats=card.Stats+"\n"+ability};
        }finally{_historyRules=previous;}
    }
    private void RefreshPlayActions()
    {
        PlayActions.Clear();if(_play is null||_play.Environment.State.Phase==Phase.Complete)return;
        var state=_play.Environment.State;var filter=PlayKind.SelectedItem as string??"All";
        foreach(var action in _play.Environment.GetLegalActions().Where(x=>filter=="All"||x.Kind.ToString()==filter)){
            var text=action.Kind switch {
                Move.Buy=>$"Buy shop {action.Source+1} → {(action.Target>=0?$"slot {action.Target+1}":"overflow")}",
                Move.BuyItem=>$"Use item from shop {action.Source+1}",Move.Sell=>$"Sell slot {action.Source+1}",
                Move.Move=>$"Move / swap slot {action.Source+1} → {action.Target+1}",Move.Merge=>$"Merge slot {action.Source+1} into {action.Target+1}",
                Move.Choose=>state.Choices[action.Choice].Label+(action.Target<0?"":$" → slot {action.Target+1}"),
                Move.Lock=>state.Locked?"Unlock shop":"Lock shop",Move.Reroll=>"Reroll shop",_=>"Finish shopping and battle"};
            PlayActions.Add(new(action.Kind.ToString(),text,action));
        }
    }
    private void ChangePlayKind(object sender,SelectionChangedEventArgs e)=>RefreshPlayActions();
    private async void ExecutePlayAction(object sender,RoutedEventArgs e)
    {
        if(_play is null||_playBusy||PlayActionList.SelectedItem is not PlayActionRow row)return;
        try{
            _playBusy=true;var step=await Task.Run(()=>_play.Apply(row.Action));
            if(step.Battle is {} battle)PlayResult=$"{(battle.PlayerWon?"Win":"Loss")} · {battle.Seconds:0.##}s · HP {battle.PlayerHp:0.##} vs {battle.OpponentHp:0.##}";
            SessionStore.Save(_play.Session);DisplayPlay();RefreshPlaySessions();
        }catch(Exception error){Status=error.Message;}finally{_playBusy=false;}
    }
    private void SavePlaySession(object sender,RoutedEventArgs e)
    {try{if(_play is not null){SessionStore.Save(_play.Session);RefreshPlaySessions();Status="Playable session saved.";}}catch(Exception error){Status=error.Message;}}
    private void RefreshPlaySessions()
    {
        PlaySessions.Clear();foreach(var s in SessionStore.List())PlaySessions.Add(new(s.Id,s.Trainer,s.Current.Wins,s.Current.Lives,s.Current.Day,s.InOpponentPool,s.Enabled));
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
            _selected=new(new(session.Id,-1,-1,session.Trainer,session.Current.Wins,session.Current.Lives,session.Current.Day,
                session.Current.Wins>=10,false,DateTimeOffset.UtcNow,session.RulesHash),session.Days);
            SelectedRunLabel=$"Your session · {session.Trainer} · {session.Decisions.Length} actions";Tabs.SelectedIndex=1;DisplayDay();
        }catch(Exception error){Status=error.Message;}
    }
    private void ChangePool(object sender,RoutedEventArgs e)
    {
        if(_playBusy||PlaySessionList.SelectedItem is not PlaySessionRow row)return;
        try{var store=SessionStore;var session=store.List().Single(x=>x.Id==row.Id);var command=((Button)sender).Tag?.ToString();
            var changed=command switch {"add"=>session with {InOpponentPool=true},"remove"=>session with {InOpponentPool=false},_=>session with {Enabled=!session.Enabled}};
            if(changed.InOpponentPool){if(changed.Days.Length==0)throw new InvalidOperationException("Complete at least one battle before adding boards.");store.Validate(changed);}
            store.Save(changed);if(_play?.Session.Id==changed.Id)_play.SetPool(changed.InOpponentPool,changed.Enabled);RefreshPlaySessions();
            Status="Opponent pool updated. Training and evaluation load it at the next launch.";
        }catch(Exception error){Status=error.Message;}
    }
}
