# fable-import-react-native-image-picker

Fable bindings for React Native Image Picker

## Installation

Install [fable-import-react-native](https://www.npmjs.com/package/fable-import-react-native) and follow the instructions for that package.

Follow install instructions for [react-native-image-picker](https://github.com/marcshilling/react-native-image-picker) and then:

```sh
$ npm install --save-dev fable-import-react-native-image-picker
```

## Usage

Follow instructions for [react-native-image-picker](https://github.com/marcshilling/react-native-image-picker).

### In F# project (.fsproj)

```xml
  <ItemGroup>
    <Compile Include="node_modules/able-import-react-native-image-picker/Fable.Import.ReactNativeImagePicker.fs" />
    <Compile Include="node_modules/able-import-react-native-image-picker/Fable.Helpers.ReactNativeImagePicker.fs" />
  </ItemGroup>
```

### In F# script (.fsx)

```fsharp
#load "node_modules/able-import-react-native-image-picker/Fable.Import.ReactNativeImagePicker.fs"
#load "node_modules/able-import-react-native-image-picker/Fable.Helpers.ReactNativeImagePicker.fs"

open Fable.Core
open Fable.Import
module R = Fable.Helpers.React
module RN = Fable.Import.ReactNative
type IP = ReactImagePicker.Globals
open Fable.Helpers.ReactNativeImagePicker
open Fable.Helpers.ReactNativeImagePicker.Props

...

showImagePicker
  [Title "Image picker"; AllowsEditing true]
  (fun result -> 
    if not result.didCancel then
        if String.IsNullOrEmpty result.error then
            console.log("Image Uri: " + result.uri)
        else
            console.log("Error: " + result.error)
    else
        console.log("dialog canceled"))
```