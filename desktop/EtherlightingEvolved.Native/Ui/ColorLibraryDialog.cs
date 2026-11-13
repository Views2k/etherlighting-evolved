using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using U=EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Ui;

internal static class ColorLibraryDialog
{
    internal const string ColorPreferences="ee.ui-preferences.v1";
    internal static void Open(AppShell shell,string label,string start,Action<string> onChange)
    {
        var form=new DialogForm(shell,label,390,pinActions: true);var current=ColorMath.NormalizeHex(start)??"#FFFFFF";
        var hsv=ColorMath.HexToHsv(current);var plane=new Spectrum();var hex=U.Input(current,"#409EFF");hex.MaxLength=7;
        var hue=U.Range(0,359,1,hsv[0]);AutomationProperties.SetName(hue,"Hue");
        var rgb=Enumerable.Range(0,3).Select(i=>{var field=U.Input();field.Width=72;field.MaxLength=3;return field;}).ToArray();
        var channels=U.Wrap(8);for(int i=0;i<3;i++)channels.Children.Add(U.Field(new[]{"Red","Green","Blue"}[i],rgb[i]));
        var library=U.Column(8);bool updating=false;
        JsonObject Store()=>Json.Parse(Preferences.GetItem(ColorPreferences)??"{}").Obj();
        void Save(JsonObject state)=>Preferences.SetItem(ColorPreferences,state.ToJsonString());
        var selectedPalette="";JsonObject? undo=null;
        Action? refreshColorChoices = null;
        void Display(bool emit=true){
            var previous = current;
            updating=true;try{
                hue.Value=hsv[0];plane.Update(hsv);current=ColorMath.HsvToHex(hsv[0],hsv[1],hsv[2]);hex.Text=current;
                var color=ColorMath.HexToRgb(current);for(int i=0;i<3;i++)rgb[i].Text=color[i].ToString("0");
            }finally{updating=false;}
            if(emit && current != previous){
                refreshColorChoices?.Invoke();
                onChange(current);
            }
        }
        void SetColor(string value){hsv=ColorMath.HexToHsv(value);Display();}
        plane.Changed+=(s,v)=>{hsv[1]=s;hsv[2]=v;Display();};
        hue.ValueChanged+=(_,_)=>{if(updating)return;hsv[0]=hue.Value;Display();};
        void CommitHex(){
            if(updating || !form.Dialog.IsOpen)return;
            var normalized=ColorMath.NormalizeHex(hex.Text);
            if(normalized is not null && normalized != current) SetColor(normalized);
            else hex.Text=current;
        }
        void CommitRgb(){
            if(updating || !form.Dialog.IsOpen)return;
            if(rgb.All(x=>byte.TryParse(x.Text,out _))){
                var value=ColorMath.RgbToHex(rgb.Select(x=>(double)byte.Parse(x.Text!)).ToArray());
                if(value != current) SetColor(value);
            }
            else Display(false);
        }
        hex.LostFocus+=(_,_)=>CommitHex();
        foreach(var channel in rgb)channel.LostFocus+=(_,_)=>CommitRgb();
        Button Swatch(string color,Action clicked){
            var tile=U.Button("","quiet",clicked,tip:color);tile.Width=32;tile.Height=30;tile.Padding=new Thickness(2);
            tile.Content=new Border{Width=24,Height=22,Background=U.Brush(color),CornerRadius=new CornerRadius(4)};
            AutomationProperties.SetName(tile,"Use "+color);return tile;
        }
        void PaintLibrary(){
            if(!form.Dialog.IsOpen)return;
            var prefs=Store();var fav=prefs.Arr("favoriteColors").Select(n=>n.Str()!).Where(s=>s is not null).ToList();
            var palettes=prefs.Objects("palettes").ToList();library.Children.Clear();
            var favoriteButton=form.Btn("Save color",()=>{
                var fresh=Store();var items=fresh.Arr("favoriteColors").Select(n=>n.Str()!).ToList();
                if(!items.Remove(current)){if(items.Count>=24)throw new InvalidOperationException("Remove a favorite before adding another.");items.Add(current);}
                fresh["favoriteColors"]=Js.StringArray(items);Save(fresh);PaintLibrary();return Task.CompletedTask;
            });
            library.Children.Add(U.Wrap(8,U.H3("Favorites"),favoriteButton));
            var chips=U.Wrap(5);foreach(var c in fav)chips.Children.Add(Swatch(c,()=>SetColor(c)));library.Children.Add(fav.Count>0?chips:U.P("Save colors here for quick access.","muted"));
            var options=palettes.Select(p=>(p.Str("name")!,p.Str("name")!)).ToList();options.Insert(0,("","Choose a palette"));
            var pick=U.Select(options,selectedPalette,v=>{selectedPalette=v;PaintLibrary();});
            var chosen=palettes.FirstOrDefault(x=>x.Str("name")==selectedPalette);
            var remove=form.Btn("Delete",async()=>{
                if(chosen is null || !await shell.Overlay.Confirm("Delete palette?",$"Remove {selectedPalette} from your palettes.","Delete palette"))return;
                var fresh=Store();fresh["palettes"]=Js.ArrayOf(fresh.Objects("palettes").Where(x=>x.Str("name")!=selectedPalette));Save(fresh);undo=(JsonObject)chosen.DeepClone();selectedPalette="";PaintLibrary();
            });remove.IsEnabled=chosen is not null;library.Children.Add(U.Wrap(6,U.Field("Palette",pick),remove));
            if(chosen is not null){var colors=U.Wrap(5);foreach(var c in chosen.Arr("colors").Select(n=>n.Str()!))colors.Children.Add(Swatch(c,()=>SetColor(c)));library.Children.Add(colors);}
            if(undo is not null)library.Children.Add(form.Btn("Undo delete",()=>{
                var fresh=Store();var old=fresh.Objects("palettes").ToList();if(old.Any(x=>string.Equals(x.Str("name"),undo.Str("name"),StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("A palette already uses that name.");
                if(old.Count>=20)throw new InvalidOperationException("Remove a palette before saving another.");
                old.Add(undo);fresh["palettes"]=Js.ArrayOf(old);Save(fresh);undo=null;PaintLibrary();return Task.CompletedTask;
            }));
            var name=U.Input("","Palette name");name.MaxLength=40;var choices=U.Wrap(4);
            var currentChoice=U.Check(current,true);choices.Children.Add(currentChoice);
            var savedChoices=fav.Concat(chosen.Arr("colors").Select(n=>n.Str()!)).Distinct().Select(c=>U.Check(c)).ToList();
            foreach(var choice in savedChoices)choices.Children.Add(choice);
            refreshColorChoices=()=>{
                favoriteButton.Content=fav.Contains(current)?"Remove color":"Save color";
                currentChoice.Content=current;
                foreach(var choice in savedChoices)choice.IsVisible=!Equals(choice.Content,current);
            };
            refreshColorChoices();
            var save=form.Btn("Save palette",async()=>{
                var selected=choices.Children.OfType<CheckBox>().Where(c=>c.IsVisible && c.IsChecked==true).Select(c=>(string)c.Content!).ToList();
                var title=name.Text?.Trim();if(string.IsNullOrEmpty(title)||selected.Count==0)throw new InvalidOperationException("Enter a palette name and at least one color.");
                if(selected.Count>12)throw new InvalidOperationException("Choose up to 12 colors.");
                var fresh=Store();var arr=fresh.Objects("palettes").ToList();var existing=arr.FirstOrDefault(x=>string.Equals(x.Str("name"),title,StringComparison.OrdinalIgnoreCase));
                if(existing is not null){if(!await shell.Overlay.Confirm("Replace palette?",$"Replace the colors in {title}.","Replace palette"))return;arr.Remove(existing);}
                if(arr.Count>=20)throw new InvalidOperationException("Remove a palette before saving another.");
                arr.Add(new JsonObject{["name"]=title,["colors"]=Js.StringArray(selected)});fresh["palettes"]=Js.ArrayOf(arr);Save(fresh);selectedPalette=title;PaintLibrary();
            },"primary");
            library.Children.Add(new Expander{Header="Save palette",Content=U.Column(8,U.Field("Palette name",name),U.Text("Colors to save"),choices,save)});
        }
        form.Body.Children.Add(plane);form.Body.Children.Add(U.Field("Hue",hue));form.Body.Children.Add(channels);form.Body.Children.Add(U.Field("Hex color",hex));form.Body.Children.Add(library);
        Display(false);PaintLibrary();
        form.Buttons.Children.Add(U.Button("Done","quiet",()=>{
            if(hex.IsFocused)CommitHex();
            else if(rgb.Any(channel=>channel.IsFocused))CommitRgb();
            form.Dialog.Close();
        }));
    }
    sealed class Spectrum:Control
    {
        double hue,sat,val;
        public event Action<double,double>? Changed;
        public Spectrum(){Height=125;Focusable=true;Cursor=new Cursor(StandardCursorType.Cross);AutomationProperties.SetName(this,"Saturation and value. Use arrow keys to adjust.");}
        public void Update(double[] hsv){hue=hsv[0];sat=hsv[1];val=hsv[2];InvalidateVisual();}
        void Pick(Point pt)=>Changed?.Invoke(Math.Clamp(pt.X/Math.Max(1,Bounds.Width),0,1),1-Math.Clamp(pt.Y/Math.Max(1,Bounds.Height),0,1));
        protected override void OnPointerPressed(PointerPressedEventArgs e){base.OnPointerPressed(e);Focus();e.Pointer.Capture(this);Pick(e.GetPosition(this));e.Handled=true;}
        protected override void OnPointerMoved(PointerEventArgs e){base.OnPointerMoved(e);if(e.Pointer.Captured==this)Pick(e.GetPosition(this));}
        protected override void OnPointerReleased(PointerReleasedEventArgs e){base.OnPointerReleased(e);e.Pointer.Capture(null);}
        protected override void OnKeyDown(KeyEventArgs e){
            base.OnKeyDown(e);double delta=e.KeyModifiers.HasFlag(KeyModifiers.Shift)?.1:.01;
            switch(e.Key){case Key.Left:sat-=delta;break;case Key.Right:sat+=delta;break;case Key.Up:val+=delta;break;case Key.Down:val-=delta;break;default:return;}
            Changed?.Invoke(Math.Clamp(sat,0,1),Math.Clamp(val,0,1));e.Handled=true;
        }
        public override void Render(DrawingContext context){
            base.Render(context);
            var horizontal=new LinearGradientBrush{StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,0,RelativeUnit.Relative),GradientStops=[new GradientStop(Colors.White,0),new GradientStop(Color.Parse(ColorMath.HsvToHex(hue,1,1)),1)]};
            var vertical=new LinearGradientBrush{StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(0,1,RelativeUnit.Relative),GradientStops=[new GradientStop(Color.FromArgb(0,0,0,0),0),new GradientStop(Colors.Black,1)]};
            var rect=new Rect(Bounds.Size);context.DrawRectangle(horizontal,null,rect,5,5);context.DrawRectangle(vertical,null,rect,5,5);
            var pos=new Point(Math.Clamp(sat*Bounds.Width,3,Math.Max(3,Bounds.Width-3)),Math.Clamp((1-val)*Bounds.Height,3,Math.Max(3,Bounds.Height-3)));
            context.DrawEllipse(null,new Pen(Brushes.Black,3),pos,5,5);context.DrawEllipse(null,new Pen(Brushes.White,1.5),pos,5,5);
        }
    }
}
