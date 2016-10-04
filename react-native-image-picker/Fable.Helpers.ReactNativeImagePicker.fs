[<Fable.Core.Erase>]
module Fable.Helpers.ReactNativeImagePicker

open Fable.Core
open Fable.Import
open Fable.Import.ReactNativeImagePicker
type IP = ReactNativeImagePicker.Globals

module Props =
    [<KeyValueList>]
    type IImagePickerOptions =
        interface end

    [<KeyValueList>]
    type ImagePickerOptions =
    | Title of string
    | CancelButtonTitle of string
    | TakePhotoButtonTitle of string
    | ChooseFromLibraryButtonTitle of string
    | CameraType of CameraType
    | MediaType of MediaType
    | MaxWidth of int
    | MaxHeight of int
    | Quality of float
    | VideoQuality of VideoQuality
    | DurationLimit of int
    | Rotation of int
    | AllowsEditing of bool
    | NoData of bool
    | StorageOptions of StorageOptions
        interface IImagePickerOptions

open Props

let inline showImagePicker (props: IImagePickerOptions list) f =
    IP.ImagePicker.showImagePicker(props |> unbox, f)

let inline showImagePickerAsync (props: IImagePickerOptions list) =
    Async.FromContinuations(fun (onSuccess, onError, _) ->
        showImagePicker
            props
            (fun result ->
                if not result.didCancel then
                    if System.String.IsNullOrEmpty result.error then
                        onSuccess (Some result.uri)
                    else
                        onError (System.Exception result.error)
                else onSuccess None)
    )

