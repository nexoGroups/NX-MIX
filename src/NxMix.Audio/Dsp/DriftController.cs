namespace NxMix.Audio.Dsp;

/// <summary>PI-Regler, der den mittleren Fuellstand eines Ringpuffers regelt, indem er das Resampling-Verhaeltnis minimal veraendert
/// (Taktdrift zweier Geraete). Ausgabe: relative Korrektur c (dimensionslos; ratio = nominal * (1 + c)).
///
/// Modell: dFuellstand/dt = -Rc * c + Drift (Rc = Ausgangs-Framerate des Konsumenten). Mit
///   c = Kp*e + Ki*Integral(e), e = Fuellstand_gefiltert - Sollwert,
///   Kp = 2*zeta*omega/Rc, Ki = omega^2/Rc
/// ergibt sich ein Regelkreis 2. Ordnung mit Eigenfrequenz omega und Daempfung zeta. Der Fuellstand wird zweistufig tiefpassgefiltert
/// (2 x tau/2, Gesamtverzoegerung tau); omega liegt weit unter 1/tau, damit die Phasenreserve gross bleibt (~58 Grad bei den
/// Standardwerten). Die langsame Regelung (Bandbreite 0,05 Hz) vermeidet hoerbare Tonhoehenmodulation.
///
/// Sanfter Start: Start() setzt Filter UND Sollwert auf den ersten gemessenen Mittelwert; der Sollwert wandert danach nur mit
/// begrenzter Geschwindigkeit (Standard: 100 ppm der Framerate) zum eigentlichen Ziel. So entsteht beim Anlauf kein Tonhoehen-Ausschlag
/// durch einen Anfangsversatz des Fuellstands.</summary>
internal sealed class DriftController
{
    private readonly double _kp, _ki, _target, _tauStage, _maxC, _slewPerSecond;
    private double _setpoint;
    private double _f1, _f2;
    private double _integral;
    private double _c;

    public DriftController(double outputRate, double targetFrames, double bandwidthHz = 0.05, double zeta = 1.0,
        double filterTauSeconds = 0.5, double maxPpm = 2000.0, double setpointSlewPpm = 100.0)
    {
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
        double omega = 2.0 * Math.PI * bandwidthHz;
        _kp = 2.0 * zeta * omega / outputRate;
        _ki = omega * omega / outputRate;
        _target = targetFrames;
        _setpoint = targetFrames;
        _tauStage = filterTauSeconds / 2.0;
        _maxC = maxPpm * 1e-6;
        _slewPerSecond = outputRate * setpointSlewPpm * 1e-6;
        _f1 = _f2 = targetFrames;
    }

    /// <summary>Aktuelle Korrektur in ppm (positiv = schneller verbrauchen).</summary>
    public double CorrectionPpm => _c * 1e6;

    /// <summary>Tiefpassgefilterter mittlerer Fuellstand in Frames.</summary>
    public double FilteredFill => _f2;

    /// <summary>Endgueltiger Sollwert in Frames.</summary>
    public double Target => _target;

    /// <summary>Momentaner (wandernder) Sollwert in Frames.</summary>
    public double Setpoint => _setpoint;

    /// <summary>Beginnt (neu) auf dem gemessenen Mittelwert. Der Integralanteil (= Drift-Schaetzung) bleibt erhalten.</summary>
    public void Start(double measuredMean)
    {
        _f1 = _f2 = measuredMean;
        _setpoint = measuredMean;
    }

    /// <summary>Wird einmal pro Messfenster aufgerufen. meanFill = mittlerer Fuellstand im Fenster, dt in Sekunden.</summary>
    public double Update(double meanFill, double dt)
    {
        double alpha = 1.0 - Math.Exp(-dt / _tauStage);
        _f1 += alpha * (meanFill - _f1);
        _f2 += alpha * (_f1 - _f2);

        double step = _slewPerSecond * dt;
        _setpoint += Math.Clamp(_target - _setpoint, -step, step);

        double e = _f2 - _setpoint;
        _integral += e * dt;
        double c = _kp * e + _ki * _integral;
        if (c > _maxC)
        {
            c = _maxC;
            _integral = (c - _kp * e) / _ki; // Anti-Windup
        }
        else if (c < -_maxC)
        {
            c = -_maxC;
            _integral = (c - _kp * e) / _ki;
        }
        _c = c;
        return c;
    }
}
