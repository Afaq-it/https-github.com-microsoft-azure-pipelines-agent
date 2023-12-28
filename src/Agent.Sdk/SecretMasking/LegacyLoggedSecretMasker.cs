﻿using Agent.Sdk.SecretMasking;
using Microsoft.TeamFoundation.DistributedTask.Logging;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

using ISecretMasker = Microsoft.TeamFoundation.DistributedTask.Logging.ISecretMasker;

namespace Agent.Sdk.Util.SecretMasking;


/// <summary>
/// Extended secret masker service, that allows to log origins of secrets
/// </summary>
public class LegacyLoggedSecretMasker : ILoggedSecretMasker
{
    private ISecretMasker _secretMasker;
    private ITraceWriter _trace;

    private void Trace(string msg)
    {
        this._trace?.Info(msg);
    }

    public LegacyLoggedSecretMasker(ISecretMasker secretMasker)
    {
        this._secretMasker = secretMasker;
    }

    public void SetTrace(ITraceWriter trace)
    {
        this._trace = trace;
    }

    public void AddValue(string pattern)
    {
        this._secretMasker.AddValue(pattern);
    }

    /// <summary>
    /// Overloading of AddValue method with additional logic for logging origin of provided secret
    /// </summary>
    /// <param name="value">Secret to be added</param>
    /// <param name="origin">Origin of the secret</param>
    public void AddValue(string value, string origin)
    {
        this.Trace($"Setting up value for origin: {origin}");
        if (value == null)
        {
            this.Trace($"Value is empty.");
            return;
        }

        AddValue(value);
    }

    public void AddRegex(string pattern)
    {
        this._secretMasker.AddRegex(pattern);
    }

    /// <summary>
    /// Overloading of AddRegex method with additional logic for logging origin of provided secret
    /// </summary>
    /// <param name="pattern"></param>
    /// <param name="origin"></param>
    public void AddRegex(string pattern, string origin, string moniker = null, ISet<string> sniffLiterals = null, RegexOptions regexOptions = 0)
    {
        this.Trace($"Setting up regex for origin: {origin}.");
        if (pattern == null)
        {
            this.Trace($"Pattern is empty.");
            return;
        }

        AddRegex(pattern);
    }

    // We don't allow to skip secrets longer than 5 characters.
    // Note: the secret that will be ignored is of length n-1.
    public static int MinSecretLengthLimit => 6;

    public int MinSecretLength
    {
        get
        {
            return _secretMasker.MinSecretLength;
        }
        set
        {
            if (value > MinSecretLengthLimit)
            {
                _secretMasker.MinSecretLength = MinSecretLengthLimit;
            }
            else
            {
                _secretMasker.MinSecretLength = value;
            }
        }
    }

    public void RemoveShortSecretsFromDictionary()
    {
        this._trace?.Info("Removing short secrets from masking dictionary");
        _secretMasker.RemoveShortSecretsFromDictionary();
    }

    public void AddValueEncoder(ValueEncoder encoder)
    {
        this._secretMasker.AddValueEncoder(encoder);
    }

    /// <summary>
    /// Overloading of AddValueEncoder method with additional logic for logging origin of provided secret
    /// </summary>
    /// <param name="encoder"></param>
    /// <param name="origin"></param>
    public void AddValueEncoder(ValueEncoder encoder, string origin)
    {
        this.Trace($"Setting up value for origin: {origin}");
        this.Trace($"Length: {encoder.ToString().Length}.");
        if (encoder == null)
        {
            this.Trace($"Encoder is empty.");
            return;
        }

        AddValueEncoder(encoder);
    }

    public ISecretMasker Clone()
    {
        return new LegacyLoggedSecretMasker(this._secretMasker.Clone());
    }

    public double elapsedMaskingTime;


    public string MaskSecrets(string input)
    {
        var stopwatch = Stopwatch.StartNew();
        string result = this._secretMasker.MaskSecrets(input);
        Interlocked.Exchange(ref elapsedMaskingTime, stopwatch.ElapsedTicks);
        return result;
    }

    Sdk.SecretMasking.ISecretMasker Sdk.SecretMasking.ISecretMasker.Clone()
    {
        return new LegacyLoggedSecretMasker(this._secretMasker.Clone());
    }

    public IDictionary<string, string> GetTelemetry()
    {
        var result = new Dictionary<string, string>
        {
            { "ElapsedMaskingTime", elapsedMaskingTime.ToString() },
        };

        return result;
    }

    public void AddRegex(string pattern, ISet<string> sniffLiterals, RegexOptions regexOptions, string origin)
    {
        throw new System.NotImplementedException();
    }
}