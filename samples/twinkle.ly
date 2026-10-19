\version "2.24.0"

\header {
  title = "Twinkle, Twinkle, Little Star"
  composer = "Traditional"
}

\score {
  \new Staff = "Melody" {
    \relative c' {
      \clef treble
      \key c \major
      \time 4/4
      c4 c g' g | a a g2 |
      f4 f e e | d d c2 ~ |
      c4 <c e g>4 r4 d8 e |
    }
  }
}
