[<AutoOpen>]
module Hydra.Util

let inline compareSequences a b = (a,b) ||> Seq.compareWith Operators.compare
let inline expectedSameAsResult expected result = (compareSequences expected result = 0)

let (|Array|_|) (pattern:array<'a>) toMatch =
    let patternLength = Seq.length pattern
    let toMatchLength = Array.length toMatch
    if patternLength > toMatchLength then
        None
    else
        if toMatch |> Seq.take patternLength |> expectedSameAsResult pattern then
            let tail = toMatch.[patternLength..]
            Some (tail)
        else
            None

