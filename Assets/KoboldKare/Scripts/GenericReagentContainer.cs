﻿using ExitGames.Client.Photon;
using KoboldKare;
using Photon.Pun;
using Photon.Realtime;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class GenericReagentContainer : MonoBehaviourPun, IValuedGood, IPunObservable {
    [System.Serializable]
    public class InspectorReagent {
        public ScriptableReagent reagent;
        public float volume;
    }
    public enum ContainerType {
        OpenTop,
        Sealed,
        Mouth,
    }
    public enum InjectType {
        Inject,
        Spray,
        Flood,
        Metabolize,
        Vacuum,
    }
    private static bool[,] ReagentMixMatrix = new bool[,]{
        // OpenTop, Sealed, Mouth
        {  true,   true,    true }, // Inject
        {  true,   false,   true }, // Spray
        {  true,   false,   false }, // Flood
        {  true,   true,   true }, // Metabolize
        {  true,   true,   true }, // Vacuum
    };
    public static bool IsMixable(ContainerType container, InjectType injectionType) {
        return ReagentMixMatrix[(int)injectionType,(int)container];
    }
    public float startingMaxVolume = float.MaxValue;
    public float volume => contents.volume;
    public float maxVolume => contents.GetMaxVolume();
    public Color GetColor() => contents.GetColor();
    public ContainerType type;
    public UnityEvent OnChange, OnFilled, OnEmpty;
    public bool isFull => Mathf.Approximately(contents.volume, contents.GetMaxVolume());
    public bool isEmpty => Mathf.Approximately(contents.volume,0f);
    public bool IsCleaningAgent() => contents.IsCleaningAgent();
    public float GetVolumeOf(ScriptableReagent reagent) => contents.GetVolumeOf(reagent);
    public float GetVolumeOf(short id) => contents.GetVolumeOf(id);
    public InspectorReagent[] startingReagents;
    [SerializeField]
    private ReagentContents contents;
    private bool filled = false;
    private bool emptied = false;
    public void Awake() {
        contents = new ReagentContents(startingMaxVolume);
    }
    public void Start() {
        foreach(var reagent in startingReagents) {
            AddMix(reagent.reagent, reagent.volume, InjectType.Inject);
        }
        filled = isFull;
        emptied = isEmpty;
    }
    public ReagentContents Spill(float spillVolume) {
        ReagentContents spillContents = contents.Spill(spillVolume);
        OnReagentContentsChanged();
        return spillContents;
    }

    public void TransferMix(GenericReagentContainer injector, float amount) {
        ReagentContents spill = injector.Spill(amount);
        AddMix(spill, InjectType.Inject);
    }
    public bool AddMix(ScriptableReagent incomingReagent, float volume, InjectType injectType) {
        if (!IsMixable(type, injectType)) {
            return false;
        }
        contents.AddMix(ReagentDatabase.GetID(incomingReagent), volume);
        OnReagentContentsChanged();
        return true;
    }
    public bool AddMix(ReagentContents incomingReagents, InjectType injectType) {
        if (!IsMixable(type, injectType)) {
            return false;
        }
        contents.AddMix(incomingReagents, this);
        OnReagentContentsChanged();
        return true;
    }
    public ReagentContents Peek() => new ReagentContents(contents);
    public ReagentContents Metabolize(float deltaTime) => contents.Metabolize(deltaTime);
    public void OverrideReagent(Reagent r) => contents.OverrideReagent(r.id, r.volume);
    public void OverrideReagent(ScriptableReagent r, float volume) => contents.OverrideReagent(ReagentDatabase.GetID(r), volume);
    public void OnReagentContentsChanged() {
        if (!filled && isFull) {
            OnFilled.Invoke();
        }
        filled = isFull;
        OnChange.Invoke();
        if (!emptied && isEmpty) {
            OnEmpty.Invoke();
        }
        emptied = isEmpty;
    }

    public float GetWorth() {
        return contents.GetValue();
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info) {
        if (stream.IsWriting) {
            stream.SendNext(contents);
        } else {
            contents = (ReagentContents)stream.ReceiveNext();
            OnReagentContentsChanged();
        }
    }
    public void OnValidate() {
        if (startingReagents == null) {
            return;
        }
        float volume = 0f;
        foreach(var reagent in startingReagents) {
            volume += reagent.volume;
        }
        startingMaxVolume = Mathf.Max(startingMaxVolume, volume);
    }
}
