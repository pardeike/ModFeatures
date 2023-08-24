# RimWorld Mod Features Library

## How to Use

To use ModFeatures, install it at startup with

```cs
using Brrainz;
// install and schedule dialog until a game is started or loaded
ModFeatures.Install<YourModClass>();  // YourModClass is the class extending 'Mod'
```

and create a folder called `Features` in your mod root (same level as `About`). Put `.png` and `.mp4` files into that folder and name them `nn_title.xxx`. For example:

- 01_Welcome.png
- 02_Introduction.mp4
- 03_Stuff.png
- 04_MoreStuff.mp4

For each file, you should have translation strings in your mod in the form of

```xml
<Feature_YourModClass_Welcome>Welcome to my mod!</Feature_YourModClass_Welcome>
<Feature_YourModClass_Introduction>Introduction</Feature_YourModClass_Introduction>
```

Make sure YourModClass matches the class name that extends `Mod`.

You can also trigger the dialog again by calling

```cs
using Brrainz;

// don't call this at startup, only on demand by the user
var showAllFeatures = true;
var unseen = ModFeatures.UnseenFeatures<YourModClass>();
if (unseen > 0 || showAllFeatures)
{
   ModFeatures.ShowAgain<YourModClass>(showAllFeatures);
}
```

That's it. You will automatically get a feature dialog displayed when the player starts or loads a game. The user can remove topics and the preferences for that are stored in

```txt
......\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModFeatures\
```

Enjoy
/Brrainz