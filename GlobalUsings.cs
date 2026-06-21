// Global usings for the Windows host project.
//
// The Core library lives in the PcVolumeControllerDashboard.Core namespace.
// Surfacing it globally lets the existing host source reference Core types
// (SerialService, and — as Phase 0 proceeds — settings, OLED rendering, and
// the audio abstraction) without sprinkling per-file usings across the
// 200 KB+ of host code during the extraction.
global using PcVolumeControllerDashboard.Core;
