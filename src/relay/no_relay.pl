#!/usr/bin/perl
# Single-process select() TCP relay (core + Socket only) tuned for Nuclear Option's
# remote-command protocol, where the GAME closes the TCP connection after every
# command. So we keep the client (bot) connection open and open a FRESH upstream to
# the game per request. When the game closes an upstream we DON'T close the client
# (that was dropping the just-written reply through the host's port-forwarder) --
# the client is reused and gets a new upstream on its next request.
#   perl no_relay.pl 0.0.0.0:5550 127.0.0.1:5504
use strict;
use warnings;
use Socket;

my ($lspec, $tspec) = @ARGV;
die "usage: no_relay.pl LHOST:LPORT THOST:TPORT\n" unless $lspec && $tspec;
my ($lh, $lp) = split /:/, $lspec, 2;
my ($th, $tp) = split /:/, $tspec, 2;

sub L { print STDERR "[relay] @_\n"; }

socket(my $srv, PF_INET, SOCK_STREAM, getprotobyname('tcp')) or die "socket: $!";
setsockopt($srv, SOL_SOCKET, SO_REUSEADDR, pack('l', 1));
# TCP keepalive so idle NAT/LB paths do not silently drop the sidecar.
eval { setsockopt($srv, SOL_SOCKET, SO_KEEPALIVE, pack('l', 1)); };
my $laddr = ($lh eq '0.0.0.0' || $lh eq '*' || $lh eq '') ? INADDR_ANY : inet_aton($lh);
bind($srv, sockaddr_in($lp, $laddr)) or die "bind: $!";
listen($srv, 16) or die "listen: $!";
$| = 1;
L("listening on $lspec -> $tspec (per-request upstream, keepalive on)");

my %fh;      # fileno -> filehandle
my %mate;    # fileno -> paired filehandle (cli<->up); a cli may be unpaired (undef)
my %role;    # fileno -> 'cli' | 'up'
my $rin = '';
vec($rin, fileno($srv), 1) = 1;

sub add_fh {
    my ($f, $r) = @_;
    my $n = fileno($f);
    $fh{$n} = $f; $role{$n} = $r; vec($rin, $n, 1) = 1;
    return $n;
}

sub drop_fh {
    my $f = shift;
    return unless defined $f;
    my $n = fileno($f);
    if (defined $n) { vec($rin, $n, 1) = 0; delete $fh{$n}; delete $role{$n}; delete $mate{$n}; }
    close $f;
}

sub open_up {
    my $cli = shift;
    my $up;
    unless (socket($up, PF_INET, SOCK_STREAM, getprotobyname('tcp'))
            && connect($up, sockaddr_in($tp, inet_aton($th)))) {
        L("upstream connect failed: $!");
        return undef;
    }
    eval { setsockopt($up, SOL_SOCKET, SO_KEEPALIVE, pack('l', 1)); };
    my $un = add_fh($up, 'up');
    $mate{fileno($cli)} = $up;
    $mate{$un} = $cli;
    return $up;
}

sub fwd {
    my ($to, $buf, $len) = @_;
    my $off = 0;
    while ($off < $len) {
        my $w = syswrite($to, $buf, $len - $off, $off);
        last if !defined $w;
        $off += $w;
    }
}

while (1) {
    my $rout = $rin;
    my $n = select($rout, undef, undef, undef);
    next if !defined $n || $n <= 0;

    if (vec($rout, fileno($srv), 1)) {
        if (accept(my $cli, $srv)) {
            eval { setsockopt($cli, SOL_SOCKET, SO_KEEPALIVE, pack('l', 1)); };
            add_fh($cli, 'cli');
            $mate{fileno($cli)} = undef;
            L("accepted cli=" . fileno($cli));
        }
    }

    for my $fno (keys %fh) {
        my $f = $fh{$fno};
        next unless defined $f;
        next unless vec($rout, $fno, 1);
        my $who = $role{$fno} || '?';
        my $buf;
        my $r = sysread($f, $buf, 65536);

        if (!defined $r || $r == 0) {                 # closed
            if ($who eq 'up') {                       # game closed this request: keep client
                my $cli = $mate{$fno};
                $mate{fileno($cli)} = undef if defined $cli;
                drop_fh($f);
            } else {                                  # client gone: tear down it + any upstream
                my $up = $mate{$fno};
                drop_fh($f);
                drop_fh($up) if defined $up;
            }
            next;
        }

        if ($who eq 'cli') {
            my $up = $mate{$fno};
            $up = open_up($f) if !defined $up;        # fresh upstream per request
            if (defined $up) { fwd($up, $buf, $r); }
            else { drop_fh($f); }
        } else {                                      # up -> cli
            my $cli = $mate{$fno};
            fwd($cli, $buf, $r) if defined $cli;
        }
    }
}
