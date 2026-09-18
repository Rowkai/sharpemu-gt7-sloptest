// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Decompile only the functions named on the command line.
//
// Blanket auto-analysis of a large title's text section (GT7's is 68 MB) costs
// hours and is almost never what an investigation needs. Import with
// -noanalysis instead and point this script at the handful of addresses under
// investigation; it defines a function at each one and prints its C.
//
//   analyzeHeadless <projdir> <proj>
//       -import <text>.bin
//       -processor x86:LE:64:default -cspec gcc
//       -loader BinaryLoader -loader-baseAddr <guest base>
//       -noanalysis
//       -scriptPath scripts/ghidra -postScript DecompAt.java 0xADDR [0xADDR...]
//
// Re-runs against an existing project use -process <file> instead of -import.
//
// "WARNING: Removing unreachable block" in the output means Ghidra could not
// resolve an indirect jump (a switch table) without analysis, so that decompile
// is incomplete rather than wrong. A wrong entry address produces the same
// symptom, so confirm the address really is a function entry first - every
// `call rel32` destination is one.

import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DecompAt extends GhidraScript {
    private static final int DECOMPILE_TIMEOUT_SECONDS = 240;

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length == 0) {
            println("usage: DecompAt.java 0xADDR [0xADDR ...]");
            return;
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.setOptions(new DecompileOptions());
        decompiler.openProgram(currentProgram);

        try {
            for (String arg : args) {
                Address address = currentProgram.getAddressFactory().getAddress(arg);
                if (address == null) {
                    println("BAD ADDR " + arg);
                    continue;
                }

                Function function = getFunctionAt(address);
                if (function == null) {
                    // -noanalysis leaves the listing bare, so the function has
                    // to be declared before the decompiler will touch it.
                    try {
                        function = createFunction(address, "sub_" + arg.replace("0x", ""));
                    } catch (Exception e) {
                        println("createFunction failed at " + arg + ": " + e.getMessage());
                    }
                }
                if (function == null) {
                    println("NO FUNCTION at " + arg);
                    continue;
                }

                println("========== " + arg + "  " + function.getName() + " ==========");
                DecompileResults results =
                    decompiler.decompileFunction(function, DECOMPILE_TIMEOUT_SECONDS, monitor);
                if (results != null && results.decompileCompleted()) {
                    println(results.getDecompiledFunction().getC());
                } else {
                    println("DECOMPILE FAILED: "
                        + (results == null ? "null results" : results.getErrorMessage()));
                }
            }
        } finally {
            decompiler.dispose();
        }
    }
}
